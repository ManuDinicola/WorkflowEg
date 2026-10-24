# BPMN support

| Area | Supported |
|---|---|
| Events | none/message/timer start, end, intermediate catch (timer, message, signal), intermediate throw (signal), boundary (timer, message, signal, error; interrupting and non-interrupting), event sub-processes |
| Tasks | service task (job worker), user task, manual task, script task (flowd-FEEL), receive task, call activity |
| Gateways | exclusive, parallel, inclusive (see `docs/inclusive-join.md`), event-based |
| Structure | embedded sub-process, multi-instance (sequential and parallel) |
| Expressions | flowd-FEEL subset: arithmetic, comparison, boolean logic, strings, lists, contexts, date/time and duration |

Anything else is parsed as an unsupported node. The validator reports it with a stable code at deploy time and rejects the deployment, so nothing fails silently at run time. Check a file offline with `flowd validate file.bpmn`.
