## 22. Conformance & acceptance tests (Given/When/Then)

Fixtures live in `testdata/bpmn/<name>.bpmn`. The `TestEngine` helper (in `tests/Flowd.Conformance.Tests`, in-memory store + `FakeTimeProvider`) provides: `Deploy(file)`, `Start(id, vars)`, `ActivateJob(type)`, `CompleteJob(key, vars)`, `AdvanceTime(d)`, `Publish(msg)`, `Assert.InstanceCompleted(key)`, `Assert.ActiveElements(key, ids...)`, `Assert.Variables(key, map)`, `Assert.HistoryContains(key, events...)`.

### Deployment
- **T-DEP-01** Valid single-task process deploys; returns version 1.
- **T-DEP-02** Re-deploying identical XML returns the same definition key and version 1.
- **T-DEP-03** Changed XML ⇒ version 2; starting without version uses v2; a running v1 instance still finishes on v1.
- **T-DEP-04** Unsupported element (`bpmn:complexGateway`) ⇒ 400 listing element id and reason.
- **T-DEP-05** Dangling `targetRef` ⇒ 400 with all errors collected in one response.
- **T-DEP-06** XML with external entity ⇒ rejected (XXE-safe).

### Execution
- **T-EXEC-01** `start → end`: instance completes immediately; history shows ACTIVATING/ACTIVE/COMPLETED for both events in order.
- **T-EXEC-02** `start → service(A) → end`: after Start, one job of type A exists; `CompleteJob` completes the instance.
- **T-EXEC-03** Exclusive gateway picks first true condition in document order (fixture with two true conditions).
- **T-EXEC-04** Exclusive gateway with no true condition and default flow ⇒ default taken.
- **T-EXEC-05** Exclusive gateway with no true condition and no default ⇒ incident `CONDITION_ERROR`; other branches unaffected.
- **T-EXEC-06** Parallel fork/join: two jobs created; completing one leaves join waiting; completing both continues exactly once.
- **T-EXEC-07** Parallel join with a late second token after the first cycle does not double-fire.
- **T-EXEC-08** `terminate` end event kills sibling active branches and completes the instance.
- **T-EXEC-09** Cancel instance with active job ⇒ job `CANCELED`, instance `TERMINATED`, subscriptions/timers removed.
- **T-EXEC-10** Output mapping writes to parent scope; input mapping variable is invisible outside the activity.
- **T-EXEC-11** Determinism: same command sequence ⇒ same history (excluding keys/timestamps) across 100 runs (property test).
- **T-EXEC-12** Loop-limit: model with a cycle of script tasks hits `LOOP_LIMIT_EXCEEDED` incident, engine remains responsive.

### Jobs
- **T-JOB-01** Activate returns at most `maxJobs`; each job is returned to only one worker under 20 concurrent activators (race test).
- **T-JOB-02** Job not completed before `deadline` becomes re-activatable; retries unchanged.
- **T-JOB-03** `FailJob(retries=2)` ⇒ job reactivatable after backoff with retries=2; `FailJob(retries=0)` ⇒ incident `JOB_NO_RETRIES`.
- **T-JOB-04** After `UpdateRetries(3)` + `ResolveIncident`, the job is activatable again and the incident is `RESOLVED`.
- **T-JOB-05** Completing a job twice ⇒ second call `409 JOB_NOT_ACTIVATED`.
- **T-JOB-06** Stale worker completion after re-activation by another worker ⇒ `409`.
- **T-JOB-07** `fetchVariables` limits returned variables.
- **T-JOB-08** Long-poll returns immediately when a job appears (< 200 ms) and returns empty after `requestTimeoutMs`.

### Expressions
- **T-EXPR-01..10** Table-driven: arithmetic, comparison with null, path/filter `items[price > 10]`, 1-based indexing, string builtins, `if/then/else`, date/duration parsing, error on undefined variable (→ `EXTRACT_VALUE_ERROR`), budget exhaustion error, no side effects possible.

### Sub-process / inclusive / incidents (M3)
- **T-SUB-01** Embedded sub-process runs its inner flow; variables set inside with local mapping are not visible after completion; propagated ones are.
- **T-INCL-01** Inclusive split with 2 of 3 conditions true then join fires once after both branches finish (and not before).
- **T-INCL-02** Inclusive join with one branch that never reaches the join (conditionally skipped) fires without waiting forever.
- **T-INC-01** `EXTRACT_VALUE_ERROR` on missing variable creates an incident; setting the variable via API + resolve proceeds.

### Time & messages (M4)
- **T-TMR-01** Intermediate timer `PT1H`: no progress at +59 min; progresses at +60 min.
- **T-TMR-02** Interrupting timer boundary on a service task terminates the task/job at due time and continues on the boundary path.
- **T-TMR-03** Non-interrupting cycle boundary `R3/PT10M` fires three times; the main activity continues.
- **T-TMR-04** Timer persists across engine restart; overdue timers fire on startup.
- **T-TMR-05** Two engine nodes: each timer fires exactly once.
- **T-MSG-01** Publish before subscription with ttl=1 min ⇒ buffered then correlated when instance reaches the catch event.
- **T-MSG-02** Publish with unmatched correlation key ⇒ no effect, expires after ttl.
- **T-MSG-03** Duplicate `messageId` rejected while buffered.
- **T-MSG-04** Message start event creates a new instance with message variables.
- **T-MSG-05** Message correlates to at most one instance per process definition.
- **T-EVG-01** Event-based gateway: message vs timer race — first wins, loser's timer/subscription removed.

### Errors, signals, user tasks (M5)
- **T-ERR-01** Worker `ThrowError("PAYMENT_DECLINED")` caught by matching error boundary; task terminated; flow continues.
- **T-ERR-02** Error thrown inside a sub-process caught by the sub-process's boundary (propagation up the scope chain).
- **T-ERR-03** No handler ⇒ incident `UNHANDLED_ERROR_EVENT`.
- **T-SIG-01** Broadcast signal wakes all waiting instances with matching signal catch events.
- **T-UT-01** User task create → claim by user A → complete with variables → instance continues; claim by B while claimed ⇒ 409.

### Composition (M6)
- **T-CALL-01** Call activity starts child instance; child completion resumes the parent; variable propagation per flags.
- **T-CALL-02** Cancelling the parent cancels child instances.
- **T-MI-01** Parallel multi-instance over `[a,b,c]` creates 3 jobs; output collection order = input order even if completed in reverse.
- **T-MI-02** Sequential multi-instance runs one at a time.
- **T-MI-03** Empty collection completes immediately with empty output.
- **T-ESP-01** Message-triggered event sub-process runs while main flow continues (non-interrupting) / interrupts (interrupting).

### Security / chaos (M7)
- **T-SEC-01** Tenant A cannot read/modify tenant B's instances (404).
- **T-SEC-02** Worker role cannot deploy or cancel instances.
- **T-CHAOS-01** SIGKILL the engine at random points during a 10 000-instance run with 20 workers; at the end every instance is `COMPLETED`, no job completed twice in history, no orphan tokens.


## 23. Example fixture (for agents to copy)

`testdata/bpmn/order-simple.bpmn`

```xml
<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                  xmlns:flowd="http://flowd.io/schema/1.0"
                  id="Defs_1" targetNamespace="http://example.com/bpmn">
  <bpmn:process id="order-process" isExecutable="true">
    <bpmn:startEvent id="Start"><bpmn:outgoing>F1</bpmn:outgoing></bpmn:startEvent>

    <bpmn:serviceTask id="Charge" name="Charge card">
      <bpmn:extensionElements>
        <flowd:taskDefinition type="charge-card" retries="3"/>
        <flowd:ioMapping>
          <flowd:input source="= order.total" target="amount"/>
          <flowd:output source="= receiptId" target="receiptId"/>
        </flowd:ioMapping>
      </bpmn:extensionElements>
      <bpmn:incoming>F1</bpmn:incoming><bpmn:outgoing>F2</bpmn:outgoing>
    </bpmn:serviceTask>

    <bpmn:exclusiveGateway id="Big" default="F4">
      <bpmn:incoming>F2</bpmn:incoming><bpmn:outgoing>F3</bpmn:outgoing><bpmn:outgoing>F4</bpmn:outgoing>
    </bpmn:exclusiveGateway>

    <bpmn:serviceTask id="Review" name="Manual review">
      <bpmn:extensionElements><flowd:taskDefinition type="review-order"/></bpmn:extensionElements>
      <bpmn:incoming>F3</bpmn:incoming><bpmn:outgoing>F5</bpmn:outgoing>
    </bpmn:serviceTask>

    <bpmn:endEvent id="End"><bpmn:incoming>F4</bpmn:incoming><bpmn:incoming>F5</bpmn:incoming></bpmn:endEvent>

    <bpmn:sequenceFlow id="F1" sourceRef="Start" targetRef="Charge"/>
    <bpmn:sequenceFlow id="F2" sourceRef="Charge" targetRef="Big"/>
    <bpmn:sequenceFlow id="F3" sourceRef="Big" targetRef="Review">
      <bpmn:conditionExpression>= order.total &gt; 1000</bpmn:conditionExpression>
    </bpmn:sequenceFlow>
    <bpmn:sequenceFlow id="F4" sourceRef="Big" targetRef="End"/>
    <bpmn:sequenceFlow id="F5" sourceRef="Review" targetRef="End"/>
  </bpmn:process>
</bpmn:definitions>
```

Expected behavior: with `order.total = 50` → one `charge-card` job, then instance completes. With `order.total = 5000` → additionally a `review-order` job before completion.


