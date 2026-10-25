# flowd-client (Python)

Worker SDK for the Flowd workflow engine. Python 3.9+, standard library only (threads, `urllib`).

```python
import threading
from flowd_client import FlowdClient, JobWorker, WorkerOptions, complete, fail, bpmn_error

client = FlowdClient("http://localhost:8080", api_key="...")

def charge_card(job):
    amount = job.variables["amount"]
    if amount > 1000:
        return bpmn_error("LIMIT_EXCEEDED")                  # caught by a boundary error event
    if not charge(amount):
        return fail("card declined", retries=2, retry_backoff_ms=5000)
    return complete({"charged": True})                       # merged into the process variables

stop = threading.Event()
JobWorker(client, "charge-card", charge_card, WorkerOptions(max_concurrent_jobs=8)).run(stop)   # blocks until stop is set
```

- A handler that raises becomes a failed job with one retry fewer.
- Connection errors, 5xx and 429 are retried with jittered exponential back-off; other 4xx (for example a bad API key) end `run()` with a `FlowdHttpError`.
- Reporting a job that was already completed or taken over (409) is not an error: delivery is at-least-once, so make handlers idempotent.
- `client.start_instance("order", {"a": 1})` starts a process instance.

Test: `python -m unittest discover -s tests -v` (7 tests against a fake server).
