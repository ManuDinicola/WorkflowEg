## 6. BPMN support matrix & execution semantics

Legend: ✅ v1 · 🔜 milestone noted · ❌ not supported (reject at deploy)

### 6.1 Elements

| Element | Variants | Status |
|---|---|---|
| Process | executable only (`isExecutable=true`) | ✅ |
| Start Event | none, message, timer, signal | ✅ (message/timer M4, signal M5) |
| End Event | none, terminate, error, message(→job) | ✅ |
| Service Task | job-worker (`flowd:taskDefinition`) | ✅ |
| Send Task | as service task | ✅ M3 |
| Receive Task | message | ✅ M4 |
| User Task | job-less native user task | ✅ M5 |
| Script Task | expression-only (`flowd:script` = expression) | ✅ M3 |
| Business Rule Task | DMN decision-table (call decision) | 🔜 post-v1 (or worker-based in v1) |
| Manual Task | pass-through | ✅ |
| Exclusive Gateway | with conditions + default flow | ✅ |
| Parallel Gateway | fork/join | ✅ |
| Inclusive Gateway | fork/join | ✅ M3 |
| Event-Based Gateway | timer/message catch targets | ✅ M4 |
| Intermediate Catch | timer, message, signal | ✅ M4 |
| Intermediate Throw | none, message(→job), signal | ✅ M5 |
| Boundary Event | timer, message, error, signal; interrupting & non-interrupting | ✅ M4/M5 |
| Embedded Sub-Process | | ✅ M3 |
| Event Sub-Process | message/timer/error triggered | 🔜 M6 |
| Call Activity | by `calledElement` (latest or pinned version), variable mapping | ✅ M6 |
| Multi-Instance | parallel & sequential, over collection | ✅ M6 |
| Compensation, Transaction, Ad-hoc SP, Complex Gateway | | ❌ |

### 6.2 Execution semantics (normative)

**FR-EXEC-001 Start.** Starting an instance creates a PI, a root scope, applies provided variables to the root scope, then activates the (single, none-type) start event — or the specified start event if the definition has multiple and `startElementId` is given.

**FR-EXEC-002 Sequence flow.** When an activity completes, a token is created on **every** outgoing sequence flow of non-gateway elements (implicit parallel split). Conditions on outgoing flows of non-gateway elements MUST be rejected at deploy time unless the model is a *conditional flow* (not supported v1).

**FR-EXEC-003 Exclusive gateway.** Evaluate outgoing flow conditions **in document order**; take the first that evaluates to `true`. If none and a default flow exists, take default. If none: raise incident `CONDITION_ERROR` ("no outgoing flow matched") at the gateway. A condition evaluating to a non-boolean is an incident `CONDITION_ERROR`. Diverging with one outgoing flow and no condition is allowed (pass-through).

**FR-EXEC-004 Parallel gateway.**
- Diverging: emit one token per outgoing flow. Conditions are ignored/rejected at deploy.
- Converging (join): wait until a token has arrived on **each** incoming flow, then consume exactly one token per incoming flow and emit one token per outgoing flow. Extra tokens on a flow remain queued for the next join cycle.

**FR-EXEC-005 Inclusive gateway.**
- Diverging: evaluate all conditions; emit tokens for all that are `true`; if none, use default; if still none → incident.
- Converging: fire when no other token can still reach this gateway via a path from any *other* active token (reachability analysis over the static graph restricted to the current scope). Document the algorithm in `docs/inclusive-join.md`.

**FR-EXEC-006 Service task.** On activation: evaluate `flowd:taskDefinition type` and `retries` (static or expression), evaluate input mappings (Section 8.4) into the task-local scope, create a Job in `ACTIVATABLE`. The token waits. On job completion: apply output mappings, complete the activity, continue.

**FR-EXEC-007 Script task.** Evaluate `flowd:script expression` with variables in scope, store result in `resultVariable`. Failure → incident `EXTRACT_VALUE_ERROR`.

**FR-EXEC-008 End events.**
- None: consume token; the scope completes when it has no remaining tokens/active children. Root scope completion ⇒ PI `COMPLETED`.
- Terminate: terminate **all** active element instances in the enclosing scope, then complete the scope.
- Error end event: throw BPMN error with code (Section 12.3).

**FR-EXEC-009 Scope completion.** A scope (process, sub-process) completes iff it has zero active element instances and zero tokens waiting at joins that can never fire. Unmatched join tokens at completion ⇒ incident `UNCOMPLETABLE_JOIN`? **Decision:** log a warning event and complete the scope anyway (matches Zeebe behavior); record in `DECISIONS.md`.

**FR-EXEC-010 Sub-process.** Activation creates a child scope; its start event is activated. Variables: child scope inherits parent variables (read), writes go to the scope where the variable exists, else to the *local* scope unless output mapping targets propagation (see 8.3).

**FR-EXEC-011 Call activity.** Activation starts a new PI (child) of the called process (`calledElement` = `bpmnProcessId`; `flowd:propagateAllChildVariables` default `true`; `propagateAllParentVariables` default `true`). Child completion completes the call activity; child termination/failure with unhandled error propagates the BPMN error to the parent element. Cancelling the parent cancels children (cascade).

**FR-EXEC-012 Multi-instance.**
- `flowd:loopCharacteristics` `inputCollection` (expression → array), `inputElement` (variable name per iteration), `outputElement` (expression per iteration), `outputCollection` (variable name).
- Parallel: create all body instances at once; sequential: one after another.
- Output collection preserves input order regardless of completion order.
- Empty input collection ⇒ activity completes immediately with an empty output collection.
- Completion condition (`completionCondition`) MAY be supported in M6; if present and true after an iteration, remaining instances are terminated.

**FR-EXEC-013 Boundary events.**
- Attached to an activity; the boundary subscription/timer is created **when the activity is activated** and removed when it completes or terminates.
- Interrupting: when triggered, terminate the attached activity (and its subtree, jobs, subscriptions), then continue from the boundary event's outgoing flows.
- Non-interrupting: spawn a new token from the boundary event while the activity continues. Timer cycle (`R3/PT10M`) allowed on non-interrupting only.

**FR-EXEC-014 Event-based gateway.** On activation create all subscriptions/timers for its target catch events; the **first** to trigger wins; all other subscriptions/timers are removed; the token continues from the winning event.

**FR-EXEC-015 Cancel.** `CancelInstance` terminates all element instances depth-first (leaves first), cancels jobs (`CANCELED`), removes timers/subscriptions, cascades to child instances, and sets PI `TERMINATED`.

**FR-EXEC-016 Determinism.** Given identical (state, command, now, id sequence), the interpreter MUST produce identical output. Property-based tests SHOULD assert this.


## 7. Interpreter algorithm (reference)

```
processCommand(cmd):
  tx.begin
  inst = lockInstance(cmd.instanceKey)            # SELECT FOR UPDATE
  state = load(inst)                              # elements, variables, subs, timers
  queue = [initialStepFrom(cmd)]
  while queue not empty and steps < LIMIT:
      step = queue.pop()
      effects = interpreter.apply(state, step)    # pure
      state.mutate(effects.stateChanges)
      queue.push(effects.followUpSteps)           # e.g. ACTIVATE next element
      outbox.add(effects.externalEvents)
      history.add(effects.historyRecords)
  persist(state diff)
  tx.commit
```

- **FR-INT-001** Steps are processed FIFO per command (breadth-first) to give predictable ordering of parallel branches; ordering within a command MUST be stable and documented.
- **FR-INT-002** Incident creation inside a step MUST NOT roll back the transaction; it records the incident, leaves the token in its current state, and stops that branch only.
- **FR-INT-003** Resolving an incident (`POST /incidents/{key}/resolve`) re-queues the failed step.


