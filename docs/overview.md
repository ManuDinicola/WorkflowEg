# What Flowd is, what it is for, and how to use it

## What it is
Flowd is a workflow engine. You draw a business process in a BPMN 2.0 diagram (any modeler, such as Camunda Modeler or bpmn.io), deploy the `.bpmn` file to Flowd over REST, and Flowd runs one *process instance* per business case. State is stored durably in PostgreSQL, so a restart or crash never loses a running process.

Flowd does not run your business code. Steps that need code are *service tasks*: Flowd creates a job, your **worker** (a small program using `Flowd.Worker.Client` or plain HTTP) fetches it by type, does the work, and reports back.

## What it is for
- Long-running, multi-step processes: order handling, onboarding, approvals, claims, payments with retries and timeouts.
- Coordinating several services without hand-written state machines: branching, parallel work, waiting for a message or a timer, human tasks, error handling and compensation paths.
- Seeing the state of every case (current step, variables, history, incidents) through one API.

Not for: sub-second stateless request handling, or ad-hoc data pipelines.

## How it fits together
```
 modeler --.bpmn--> POST /v1/deployments --> Flowd (API + engine) --> PostgreSQL
                                                  ^   |
        POST /v1/process-instances (start) -------+   | jobs, timers, messages
                                                      v
                                           your workers (Flowd.Worker.Client)
```

## Five-minute tour
1. **Run it** (see README "Run"): `flowd migrate`, then `flowd serve`. For local experiments add `--insecure-no-auth`; never do that in production.
2. **Deploy** a process: `curl -F resources=@order.bpmn http://localhost:8080/v1/deployments`.
3. **Start an instance**: `POST /v1/process-instances` with `{"bpmnProcessId":"order","variables":{"amount":120}}`.
4. **Work the jobs** with the SDK:
```csharp
var client = FlowdClient.Create(new Uri("http://localhost:8080"), apiKey);
var worker = client.CreateWorker("charge-card", (job, ct) =>
{
    var amount = job.Variables["amount"]!.GetValue<decimal>();
    if (amount > 1000) return Task.FromResult(JobResults.BpmnError("LIMIT_EXCEEDED"));   // caught by a boundary error event
    return Task.FromResult(JobResults.Complete(new JsonObject { ["charged"] = true }));  // merged into the process variables
});
await worker.RunAsync(stoppingToken);   // long polls; stops gracefully when cancelled
```
Delivery is at-least-once: make handlers idempotent.
5. **Observe**: `GET /v1/process-instances/{key}`, `/variables`, `/history`, `GET /v1/incidents`, and `/metrics` for Prometheus.

## Where to read more
- Architecture and layering: `docs/architecture.md`
- Supported BPMN elements: `docs/bpmn-support.md`
- Running it in production (Windows service, security, backup, retention): `docs/operations.md`
- Endpoints: `docs/openapi-routes.txt` (contract snapshot) and `GET /openapi/v1.json` on a running host
- Performance numbers: `docs/benchmarks.md`
