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
