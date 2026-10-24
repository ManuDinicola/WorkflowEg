# @flowd/client

Worker SDK for the Flowd workflow engine, for Node.js 20+ (uses the built-in `fetch`, no runtime dependencies).

```ts
import { FlowdClient, createWorker, complete, fail, bpmnError } from "@flowd/client";

const client = new FlowdClient("http://localhost:8080", { apiKey: process.env.FLOWD_API_KEY });

const worker = createWorker(client, "charge-card", async (job, signal) => {
  const amount = Number(job.variables.amount);
  if (amount > 1000) return bpmnError("LIMIT_EXCEEDED");        // caught by a boundary error event
  if (!(await charge(amount, signal))) return fail("card declined", { retries: 2, retryBackoffMs: 5000 });
  return complete({ charged: true });                           // merged into the process variables
}, { maxConcurrentJobs: 8 });

const stop = new AbortController();
process.on("SIGTERM", () => stop.abort());
await worker.run(stop.signal);   // long polls; returns after in-flight handlers have reported
```

- A handler that throws becomes a failed job with one retry fewer.
- Transport errors, 5xx and 429 are retried with jittered exponential back-off; a 4xx (for example a bad API key) ends `run()` with a `FlowdHttpError`.
- Reporting a job that was already completed or taken over (409) is not an error: delivery is at-least-once, so make handlers idempotent.
- `client.startInstance(bpmnProcessId, variables)` starts a process instance.

Build and test: `npm install && npm test` (7 tests against a fake server). `test/e2e.mjs` is a manual check against a running Flowd.
