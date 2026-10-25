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
FEEL ranges inside normal expressions, and decision versions pinned by a process.

## Calling a decision from a process
```xml
<bpmn:businessRuleTask id="Rule">
  <bpmn:extensionElements><flowd:calledDecision decisionId="discount" resultVariable="rate"/></bpmn:extensionElements>
</bpmn:businessRuleTask>
```
The task creates an internal job of type `flowd.decision`. A built-in worker (a hosted service of the engine, every 200 ms, all tenants) evaluates the latest version of the decision with the process variables and completes the job with the result in `resultVariable` (default `decisionResult`). If the decision is missing or fails, the job fails and retries like any other, then raises an incident. Delivery is at-least-once like every job.

## Decisions that need other decisions (requirements graph)
A decision can list the decisions it needs: `<informationRequirement><requiredDecision href="#rate"/></informationRequirement>`. Those decisions (in the same file) are evaluated first, recursively, and their results are available as variables under their name and their id, so `Base rate` / `rate` can be used in input expressions, output entries and literal expressions of the decision that requires them. A requirement that is not in the file answers `DMN_REQUIREMENT_MISSING`, a cycle `DMN_CYCLE`. Requirements across files and input data / knowledge source elements are not used.

## Business knowledge models
A `businessKnowledgeModel` with `encapsulatedLogic` (`formalParameter` names and a `literalExpression`) becomes a FEEL function with the model's name. Decisions of the same file call it like any function: `withTax(amount, 20)`. A model can call the models defined before it in the file. Names must be plain identifiers (no spaces), because they are called by name. Only literal expression logic is supported (not decision tables or other boxed expressions inside the model), and `knowledgeRequirement` links are not checked: every model of the file is available to every decision of the file.

## Decision table editor
The **Decisions** tab of the UI edits a decision table in a grid: inputs (label and FEEL expression), outputs, hit policy, aggregation and one row per rule (`-` means any value; text results need quotes). Open a file or a deployed decision, **Download** or **Deploy** it, and try the deployed version with a JSON of variables. The editor reads and writes one decision table per file; business knowledge models, required decisions and literal-expression decisions in an opened file are not kept when you save, and the editor tells you when the file has more decisions. `GET /v1/decisions/{id}/xml` returns the latest DMN file of a decision.

## History of evaluations
Every evaluation of a deployed decision is recorded: direct calls to `POST /v1/decisions/{id}/evaluate` (source `api`) and the business rule tasks of instances (source `task`, with the process instance key). A record holds the input variables (replaced by a size note when they exceed 64 KB), the output or the error, the time and how long the evaluation took. `GET /v1/decision-instances?decisionId=...&processInstanceKey=...&pageSize=...&after=...` lists them newest first, `GET /v1/decision-instances/{key}` returns one, and the Decisions tab of the UI has a **Show recent evaluations** button. Records are removed with the history (`Flowd__Retention__HistoryDays`, default 30). Inputs can contain personal data: the history is as sensitive as your process variables.
