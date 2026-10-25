# BPMN support

| Area | Supported |
|---|---|
| Events | none/message/timer start, end, intermediate catch (timer, message, signal), intermediate throw (signal), boundary (timer, message, signal, error; interrupting and non-interrupting), event sub-processes |
| Tasks | service task (job worker), user task, manual task, script task (flowd-FEEL), receive task, call activity |
| Gateways | exclusive, parallel, inclusive (see `docs/inclusive-join.md`), event-based |
| Structure | embedded sub-process, multi-instance (sequential and parallel) |
| Expressions | flowd-FEEL subset: arithmetic, comparison, boolean logic, strings, lists, contexts, date/time and duration |

Anything else is parsed as an unsupported node. The validator reports it with a stable code at deploy time and rejects the deployment, so nothing fails silently at run time. Check a file offline with `flowd validate file.bpmn`.

## Message throw events
An intermediate throw event or an end event with a `messageEventDefinition` (and no `flowd:taskDefinition`) publishes the message when the token arrives, then continues without waiting. The correlation key is the `flowd:subscription correlationKey` expression of the referenced `bpmn:message`, evaluated in the throwing scope; it must give a string. The message is buffered for one minute, so a receiver that starts waiting a moment later still gets it, and it is delivered like any published message (at most one instance per definition, message start events included). No variables travel with it yet. A throw event that has a `flowd:taskDefinition` still creates a worker job instead (the older behaviour); a send task is still a worker job.

## Execution listeners
Any flow node may declare `flowd:executionListeners` with `flowd:listener event="start|end" type="job-type"`. When the element starts or ends, Flowd creates an ordinary job of that type with the header `_flowdListener` (the event name). A listener job never blocks the element, cannot change variables, and a failure never raises an incident; delivery is at least once. This differs from Camunda 8.6, where listeners block the element. Use them for notifications, audit and side effects. User tasks additionally accept `created`, `assigned` and `completed` (task listeners).

## Conditional events and ad-hoc sub-processes
A `conditionalEventDefinition` with `<bpmn:condition>= expression</bpmn:condition>` is allowed on intermediate catch events, boundary events and event sub-process start events. After every command (variable change, job completion, ...) Flowd evaluates the condition in the scope of the waiting element and fires the event the first time it is true (at once if it already holds). It fires once, so a non-interrupting conditional event does not repeat. Conditional process start events are not supported.

An `adHocSubProcess` starts every inner activity that has no incoming flow, all in parallel, and completes when nothing inside is live. Camunda's dynamic activity selection and completion condition are not supported.

## Compensation (simplified)
Attach a `boundaryEvent` with `compensateEventDefinition` to an activity and point a `bpmn:association` from it to a task with `isForCompensation="true"` (the handler). A compensation throw event (intermediate or end, optionally with `activityRef`) starts the handler of every activity in its scope that has completed, latest first. Differences from the full BPMN rule: the handlers start together instead of one after the other, an intermediate throw event does not wait for them (an end event does, because the instance ends only when they are done), nested sub-process compensation and the compensation of an activity inside a loop are not supported, and an activity is compensated at most once per completion.

## Signal end events, version tags, calling by version tag
- A **signal end event** (`endEvent` with `signalEventDefinition`) broadcasts its signal when the path ends, like a signal throw event.
- A process can carry a **version tag**: `<bpmn:process ...><bpmn:extensionElements><flowd:versionTag value="stable"/></bpmn:extensionElements>`. The tag is stored with the definition and shown in `GET /v1/process-definitions`.
- A **call activity** can bind to a tag: `<flowd:calledElement bindingType="versionTag" versionTag="stable"/>` starts the newest version of the called process that was deployed with that tag; if there is none, the call activity raises an incident. Without a binding it calls the latest version, as before. The "deployment" binding of Camunda (the version deployed together with the caller) is not supported.
- The called process id of a call activity may be an expression (`= target`).
