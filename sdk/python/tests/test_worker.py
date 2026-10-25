import json
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from flowd_client import FlowdClient, FlowdHttpError, JobWorker, WorkerOptions, bpmn_error, complete, fail


def job(key, retries=3):
    return {
        "key": key,
        "type": "work",
        "processInstanceKey": "1",
        "elementId": "A",
        "retries": retries,
        "deadline": "2030-01-01T00:00:00Z",
        "variables": {"amount": 5},
        "customHeaders": {},
    }


class FakeFlowd:
    """A fake Flowd: ``activate(poll)`` decides what each poll answers; everything else is recorded."""

    def __init__(self, activate, report_status=204):
        self.calls = []
        self.polls = 0
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802 - http.server API
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length) or b"{}")
                outer.calls.append({"path": self.path, "body": body, "api_key": self.headers.get("X-Api-Key")})
                if self.path == "/v1/jobs/activate":
                    status, jobs = activate(outer.polls)
                    outer.polls += 1
                    payload = json.dumps({"jobs": jobs or []}).encode() if status == 200 else b"{}"
                    self.send_response(status)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(payload)))
                    self.end_headers()
                    self.wfile.write(payload)
                else:
                    self.send_response(report_status)
                    self.send_header("Content-Length", "0")
                    self.end_headers()

            def log_message(self, *args):  # silence the test output
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


FAST = WorkerOptions(poll_timeout_ms=20, min_backoff_ms=1, max_backoff_ms=5)


def run_until(worker, condition, timeout=5.0):
    stop = threading.Event()
    thread = threading.Thread(target=worker.run, args=(stop,))
    thread.start()
    deadline = time.time() + timeout
    while not condition() and time.time() < deadline:
        time.sleep(0.01)
    stop.set()
    thread.join(timeout=5)
    return not thread.is_alive()


class WorkerTests(unittest.TestCase):
    def test_completes_jobs_with_variables_sends_the_api_key_and_stops_gracefully(self):
        fake = FakeFlowd(lambda poll: (200, [job("1"), job("2")] if poll == 0 else []))
        worker = JobWorker(FlowdClient(fake.url, api_key="secret"), "work", lambda j: complete({"doubled": j.variables["amount"] * 2}), FAST)

        stopped = run_until(worker, lambda: worker.handled == 2)
        fake.close()

        self.assertTrue(stopped)
        completes = [c for c in fake.calls if c["path"].endswith("/complete")]
        self.assertEqual(len(completes), 2)
        self.assertEqual(completes[0]["body"]["variables"], {"doubled": 10})
        self.assertTrue(all(c["api_key"] == "secret" for c in fake.calls))

    def test_a_raising_handler_becomes_a_failed_job_with_one_retry_fewer(self):
        fake = FakeFlowd(lambda poll: (200, [job("7", 3)] if poll == 0 else []))

        def boom(_):
            raise RuntimeError("boom")

        worker = JobWorker(FlowdClient(fake.url), "work", boom, FAST)

        run_until(worker, lambda: worker.handled == 1)
        fake.close()

        failed = next(c for c in fake.calls if c["path"].endswith("/fail"))
        self.assertEqual(failed["body"]["retries"], 2)
        self.assertEqual(failed["body"]["errorMessage"], "boom")

    def test_explicit_retries_and_bpmn_errors_are_reported(self):
        fake = FakeFlowd(lambda poll: (200, [job("1"), job("2")] if poll == 0 else []))
        worker = JobWorker(
            FlowdClient(fake.url),
            "work",
            lambda j: fail("later", retries=0, retry_backoff_ms=500) if j.key == "1" else bpmn_error("LIMIT", "too much", {"why": 1}),
            FAST,
        )

        run_until(worker, lambda: worker.handled == 2)
        fake.close()

        failed = next(c for c in fake.calls if c["path"] == "/v1/jobs/1/fail")
        self.assertEqual(failed["body"]["retries"], 0)
        self.assertEqual(failed["body"]["retryBackoffMs"], 500)
        error = next(c for c in fake.calls if c["path"] == "/v1/jobs/2/error")
        self.assertEqual(error["body"]["errorCode"], "LIMIT")
        self.assertEqual(error["body"]["variables"], {"why": 1})

    def test_server_errors_are_retried_more_often_than_there_are_slots(self):
        fake = FakeFlowd(lambda poll: (503, None) if poll < 10 else (200, [job("1")] if poll == 10 else []))
        worker = JobWorker(FlowdClient(fake.url), "work", lambda j: complete(), WorkerOptions(poll_timeout_ms=20, min_backoff_ms=1, max_backoff_ms=5, max_concurrent_jobs=2))

        run_until(worker, lambda: worker.handled == 1)
        fake.close()

        self.assertEqual(worker.handled, 1)

    def test_a_rejected_api_key_ends_the_worker(self):
        fake = FakeFlowd(lambda poll: (401, None))
        worker = JobWorker(FlowdClient(fake.url), "work", lambda j: complete(), FAST)

        with self.assertRaises(FlowdHttpError) as context:
            worker.run(threading.Event())
        fake.close()

        self.assertEqual(context.exception.status, 401)

    def test_a_409_when_reporting_is_tolerated(self):
        fake = FakeFlowd(lambda poll: (200, [job("1")] if poll == 0 else []), report_status=409)
        worker = JobWorker(FlowdClient(fake.url), "work", lambda j: complete(), FAST)

        run_until(worker, lambda: worker.handled == 1)
        fake.close()

        self.assertEqual(worker.handled, 1)

    def test_start_instance_returns_the_key(self):
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802
                self.rfile.read(int(self.headers.get("Content-Length", "0")))
                payload = json.dumps({"processInstanceKey": "42"}).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()

        key = FlowdClient(f"http://127.0.0.1:{server.server_address[1]}/").start_instance("order", {"a": 1})
        server.shutdown()
        server.server_close()

        self.assertEqual(key, "42")


if __name__ == "__main__":
    unittest.main()
