## 1. Overview

### 1.1 Goals

Flowd is a **process orchestration engine**: it deploys BPMN 2.0 process definitions, starts and runs process instances durably, coordinates external **job workers** and human tasks, handles timers/messages/errors, and exposes everything through an API, with full audit history.

Core capabilities (v1):
- G1. Deploy BPMN 2.0 XML (and optionally DMN 1.3 later) with versioning.
- G2. Execute process instances with correct BPMN token semantics.
- G3. Durable state; survive crashes and restarts without losing or duplicating work (at-least-once job delivery, exactly-once state transitions).
- G4. External-worker model (long-polling job activation), like Camunda 8 / Zeebe and Camunda 7 "External Tasks".
- G5. Timers, message correlation, signals, boundary events, error handling, incidents.
- G6. REST API (+ optional gRPC), OpenAPI documented.
- G7. Horizontally scalable stateless API/engine nodes on top of PostgreSQL.
- G8. Observability: metrics, structured logs, traces, audit history.

### 1.2 Non-goals (v1)

- Graphical modeler (use bpmn.io externally). A read-only instance viewer MAY come later.
- Full FEEL or full DMN compliance (a defined subset only, Section 8).
- In-process Java-style delegates / arbitrary code execution inside the engine. **No script engines that execute untrusted code.**
- CMMN, case management, BPMN choreography/conversation diagrams.
- Event-sourced distributed log à la Zeebe (we use a relational DB; see Section 4).
- Compensation, transactions sub-processes, ad-hoc sub-processes (planned post-v1).
- Multi-region active-active.

### 1.3 Compatibility stance

Flowd implements a **documented subset of BPMN 2.0.2** and a Zeebe-flavored extension namespace for engine-specific attributes (`xmlns:flowd="http://flowd.io/schema/1.0"`). Models authored in bpmn.io/Camunda Modeler that use only supported elements MUST import cleanly. Unsupported elements MUST be rejected at deployment with a precise error (element id, type, reason), never silently ignored.


