"""Long-polling job worker."""

from __future__ import annotations

import random
import threading
import urllib.error
from concurrent.futures import ThreadPoolExecutor
from typing import Optional

from .client import FlowdClient, FlowdHttpError
from .types import Job, JobHandler, JobResult, WorkerOptions, fail


class JobWorker:
    """
    Fetches jobs, runs the handler with bounded concurrency, reports the result, retries transport errors with jittered
    exponential back-off and stops gracefully when the stop event is set. Delivery is at-least-once: handlers must be idempotent.
    """

    def __init__(self, client: FlowdClient, job_type: str, handler: JobHandler, options: Optional[WorkerOptions] = None) -> None:
        self._client = client
        self._type = job_type
        self._handler = handler
        self._options = options or WorkerOptions()
        self._lock = threading.Condition()
        self._in_flight = 0
        self._handled = 0

    @property
    def handled(self) -> int:
        """Number of jobs whose result was reported."""
        with self._lock:
            return self._handled

    def run(self, stop: threading.Event) -> None:
        """Runs until ``stop`` is set, then waits for the handlers that are still running."""
        failures = 0
        with ThreadPoolExecutor(max_workers=self._options.max_concurrent_jobs) as pool:
            while not stop.is_set():
                if not self._wait_for_free_slot(stop):
                    break
                try:
                    jobs = self._poll()
                except FlowdHttpError as error:
                    if 400 <= error.status < 500 and error.status != 429:
                        raise  # a bad key or request will not fix itself by retrying
                    failures += 1
                    stop.wait(self._backoff(failures))
                except (urllib.error.URLError, OSError, TimeoutError, ValueError):
                    failures += 1
                    stop.wait(self._backoff(failures))
                else:
                    failures = 0
                    for job in jobs:
                        with self._lock:
                            self._in_flight += 1
                        pool.submit(self._run_job, job, stop)

    def _wait_for_free_slot(self, stop: threading.Event) -> bool:
        with self._lock:
            while self._in_flight >= self._options.max_concurrent_jobs:
                if stop.is_set():
                    return False
                self._lock.wait(0.05)
        return not stop.is_set()

    def _poll(self):
        with self._lock:
            free = self._options.max_concurrent_jobs - self._in_flight
        o = self._options
        return self._client.activate_jobs(self._type, o.worker_name, o.job_timeout_ms, max(free, 1), o.poll_timeout_ms, o.fetch_variables)

    def _run_job(self, job: Job, stop: threading.Event) -> None:
        try:
            try:
                result: JobResult = self._handler(job)
            except Exception as error:  # noqa: BLE001 - a handler failure must become a failed job, never kill the worker
                result = fail(str(error))
            self._report(job, result, stop)
        finally:
            with self._lock:
                self._in_flight -= 1
                self._lock.notify_all()

    def _report(self, job: Job, result: JobResult, stop: threading.Event) -> None:
        for attempt in range(1, 6):
            try:
                self._client.report(job, result, self._options.worker_name)
            except FlowdHttpError as error:
                if error.status < 500 and error.status != 429:
                    return  # the engine refused it for good
            except (urllib.error.URLError, OSError, TimeoutError, ValueError):
                pass
            else:
                with self._lock:
                    self._handled += 1
                return
            if attempt < 5:
                time_to_wait = self._backoff(attempt)
                threading.Event().wait(time_to_wait)  # not interruptible on purpose: finish reporting even while stopping
        # give up: the job deadline hands it to another delivery (at-least-once)

    def _backoff(self, failures: int) -> float:
        o = self._options
        exponential = o.min_backoff_ms * (2 ** min(failures - 1, 10))
        return min(exponential, o.max_backoff_ms) * (0.5 + random.random() / 2) / 1000.0  # jitter between 50 % and 100 %
