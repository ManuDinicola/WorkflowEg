## 9. Jobs & worker protocol

Workers are external processes. They pull jobs over HTTP long-poll (gRPC streaming MAY follow).

### 9.1 Semantics
- **FR-JOB-001 Activation.** `ActivateJobs(type, worker, timeoutMs, maxJobs, fetchVariables[], requestTimeoutMs)` atomically moves up to `maxJobs` jobs of `type` from `ACTIVATABLE` to `ACTIVATED`, sets `deadline = now + timeoutMs`, `worker`, decrements nothing. Uses `SELECT … FOR UPDATE SKIP LOCKED`. Long-poll up to `requestTimeoutMs` (default 10 s, max 60 s) using Postgres `LISTEN/NOTIFY` (fallback: poll every 100–500 ms).
- **FR-JOB-002 Completion.** `CompleteJob(jobKey, variables)` is valid only for `ACTIVATED` jobs; applies output mapping, advances the process instance. Idempotency: completing an already `COMPLETED` job returns `409 JOB_NOT_ACTIVATED` with state info; workers must treat as success if their own prior attempt succeeded.
- **FR-JOB-003 Failure.** `FailJob(jobKey, retries, errorMessage, retryBackoffMs?)` sets remaining retries. If `retries == 0` → create incident `JOB_NO_RETRIES`, job state `FAILED`. Otherwise back to `ACTIVATABLE` after `retryBackoffMs`.
- **FR-JOB-004 Timeout.** A sweeper moves jobs with `deadline < now` back to `ACTIVATABLE` (retries **not** decremented). At-least-once delivery ⇒ workers MUST be idempotent. Document this prominently.
- **FR-JOB-005 BPMN error.** `ThrowError(jobKey, errorCode, errorMessage, variables?)` triggers error handling (Section 12.3). Job state → `ERROR_THROWN`.
- **FR-JOB-006 Update.** `UpdateRetries`, `UpdateTimeout` endpoints.
- **FR-JOB-007 Fairness.** Within a job type, activation order is by `createdAt` ascending (FIFO), best-effort.
- **FR-JOB-008 Fetch variables.** Only the requested variables (or all if unspecified) are returned to the worker, evaluated from the activity scope after input mappings.
- **FR-JOB-009 Custom headers.** Static `flowd:taskHeaders` are returned to workers with the job.

### 9.2 Reference worker SDK
Provide a minimal C# SDK `Flowd.Worker.Client` (NuGet-packable, references only `Flowd.Contracts`) with: `FlowdClient.CreateWorker(type, handler, options)`, concurrency and timeout options, automatic retry with jittered backoff, graceful shutdown via `CancellationToken`/`IHostedService` integration, `job.CompleteAsync(vars)`, `job.FailAsync(msg, retries)`, `job.ThrowErrorAsync(code, msg)`; handler results modeled as a union (`JobResult`: `Completed`, `Failed`, `BpmnError`). A TypeScript SDK `sdk/ts` is optional. (SDKs are M7; the engine MUST NOT depend on them.)


## 10. Timers

- **FR-TMR-001** Timer definitions: `timeDate` (absolute), `timeDuration` (relative to activation), `timeCycle` (`R[n]/<duration>` ISO repeating). Expressions allowed (`= …`).
- **FR-TMR-002** Scheduler loop per node: every 250 ms (configurable) `SELECT … FROM timers WHERE due_at <= now() AND state='ACTIVE' ORDER BY due_at FOR UPDATE SKIP LOCKED LIMIT N`, then process each as a command (`TriggerTimer`) in its own transaction.
- **FR-TMR-003** Timers MUST survive restarts; overdue timers fire on startup, in `dueAt` order.
- **FR-TMR-004** Time source injected; tests use a fake clock and `AdvanceTime(d)` helper in the test engine.
- **FR-TMR-005** Timer start events: on deployment of version N, create the timer; remove timers of version N-1 for the same process id.
- **FR-TMR-006** Cycle timers re-arm after each firing, with drift-free scheduling (`next = previousDue + interval`).


## 11. Messages & signals

- **FR-MSG-001 Publish.** `PublishMessage(name, correlationKey, variables, ttlMs, messageId?)`.
- **FR-MSG-002 Correlation.** A message correlates to **every distinct process definition** subscription (at most **one** instance per definition) whose `(name, correlationKey)` match. If no subscription matches now and `ttlMs > 0`, the message is buffered until TTL expiry and correlated as soon as a matching subscription opens.
- **FR-MSG-003 Idempotency.** A non-empty `messageId` MUST reject duplicates of the same `(name, correlationKey, messageId)` while the message is still buffered/not expired (`409 MESSAGE_ALREADY_EXISTS`).
- **FR-MSG-004 Message start events.** A published message that matches a message start event creates a new instance (once per buffered message; uniqueness per `(name, correlationKey)` while an instance created by that correlation is active — configurable).
- **FR-MSG-005 Correlation key** is evaluated (expression) when the subscribing element is activated; non-string results → incident `EXTRACT_VALUE_ERROR`.
- **FR-SIG-001 Signals.** `BroadcastSignal(name, variables)` triggers **all** matching signal subscriptions across instances; never buffered.


## 12. Errors, retries, incidents

### 12.1 Incident types (enum)
`JOB_NO_RETRIES`, `CONDITION_ERROR`, `EXTRACT_VALUE_ERROR`, `IO_MAPPING_ERROR`, `CALLED_ELEMENT_ERROR`, `UNHANDLED_ERROR_EVENT`, `VARIABLE_SIZE_EXCEEDED`, `LOOP_LIMIT_EXCEEDED`, `MESSAGE_SIZE_EXCEEDED`, `UNKNOWN`.

### 12.2 Rules
- **FR-INC-001** An open incident blocks only the affected token; other branches continue.
- **FR-INC-002** `ResolveIncident` after the operator fixes the cause (updated variables, job retries) re-executes the failed step; for `JOB_NO_RETRIES` the operator first calls `UpdateRetries(jobKey, n>0)`, then resolves.
- **FR-INC-003** Incidents MUST be listed/filterable via API and emit outbox events `INCIDENT_CREATED/RESOLVED`.

### 12.3 BPMN error propagation
When an error with `errorCode` is thrown (job `ThrowError` or error end event):
1. Search for a matching **error boundary event** on the throwing activity (match `errorRef.errorCode`, or catch-all if none specified), then up the scope chain (enclosing sub-process boundary events, event sub-processes M6, then call-activity parent).
2. First match wins (innermost). The matched activity is interrupted (terminated) and flow continues from the boundary.
3. No match ⇒ incident `UNHANDLED_ERROR_EVENT` at the throwing element.


