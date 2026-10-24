# Decisions (DMN)

Flowd evaluates DMN 1.x decision tables and literal expression decisions with the same flowd-FEEL engine as BPMN.

## Deploy and evaluate
```
curl -F resources=@shipping.dmn http://localhost:8080/v1/decisions
curl -X POST http://localhost:8080/v1/decisions/shipping/evaluate -H "Content-Type: application/json" -d '{"variables":{"weight":3}}'
# {"result":5}
```
Deploying the same file again keeps the version; a changed file creates the next version. `GET /v1/decisions` lists them. Evaluation always uses the latest version.

## Supported
- Hit policies: UNIQUE, FIRST, ANY, PRIORITY, COLLECT (none, SUM, MIN, MAX, COUNT), RULE ORDER, OUTPUT ORDER. PRIORITY and OUTPUT ORDER use the `outputValues` of the first output column.
- Input entries: `-` (anything), comparisons (`< 18`, `>= 3`, `!= 5`), ranges (`[1..5]`, `(1..5]`, `]1..5[`), lists (`"gold", "silver"`), `not(...)`, and any FEEL value.
- Output entries: any FEEL expression over the request variables. One output column returns the value, several return an object.
- Results: no match gives `null` (UNIQUE, FIRST, ANY, PRIORITY) or `[]` (COLLECT, RULE ORDER, OUTPUT ORDER).
- Errors: UNIQUE with several matches `DMN_MULTIPLE_MATCHES`, ANY with different outputs `DMN_ANY_CONFLICT`, failing input or output expressions `DMN_INPUT` / `DMN_OUTPUT`.

## Not yet
Decision requirements graphs (a decision calling another), FEEL ranges inside normal expressions, and decision versions pinned by a process.

## Calling a decision from a process
```xml
<bpmn:businessRuleTask id="Rule">
  <bpmn:extensionElements><flowd:calledDecision decisionId="discount" resultVariable="rate"/></bpmn:extensionElements>
</bpmn:businessRuleTask>
```
The task creates an internal job of type `flowd.decision`. A built-in worker (a hosted service of the engine, every 200 ms, all tenants) evaluates the latest version of the decision with the process variables and completes the job with the result in `resultVariable` (default `decisionResult`). If the decision is missing or fails, the job fails and retries like any other, then raises an incident. Delivery is at-least-once like every job.
