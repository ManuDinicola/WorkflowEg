## 13. Deployment & versioning

- **FR-DEP-001** `POST /v1/deployments` accepts multipart `.bpmn` (and later `.dmn`, `.form`) files. Parse → validate → persist atomically; all-or-nothing per deployment.
- **FR-DEP-002 Versioning.** If the XML checksum for a `bpmnProcessId` equals the latest version's checksum, return the existing definition (no new version, idempotent). Otherwise assign `version = latest + 1`.
- **FR-DEP-003** New instances use **latest** version unless `version` is specified. Running instances keep their definition forever.
- **FR-DEP-004 Validation (reject with 400 and list of all errors):** well-formed XML; XSD valid; each process has exactly one process id; unique element ids; all `sourceRef/targetRef` resolve; start event exists (≥1); no unsupported elements; service tasks have `taskDefinition type`; gateways with conditions on all non-default outgoing flows (exclusive/inclusive diverging); boundary events attached to supported activities; expressions parse; timer definitions parse; call activities' `calledElement` is a non-empty string (existence checked at runtime).
- **FR-DEP-005** `GET /v1/process-definitions/{key}/xml` returns the original unmodified XML.
- **FR-DEP-006** Process definition deletion is NOT supported in v1 (only `DELETE` of definitions with zero instances MAY be added behind a flag).


## 14. API

Base path `/v1`. JSON; all keys as strings; timestamps RFC 3339 UTC; errors per RFC 9457 (`application/problem+json`) with `type`, `title`, `status`, `detail`, `code` (stable machine code).

### 14.1 Endpoints (REST)

| ID | Method & path | Purpose |
|---|---|---|
| API-001 | `POST /deployments` | Deploy resources |
| API-002 | `GET /process-definitions` · `GET /process-definitions/{key}` · `/xml` | Query definitions |
| API-003 | `POST /process-instances` `{bpmnProcessId, version?, variables?, businessKey?, startElementId?}` | Start instance; returns `{processInstanceKey,…}` |
| API-004 | `GET /process-instances/{key}` · `GET /process-instances?filter…` | Query |
| API-005 | `POST /process-instances/{key}/cancel` | Cancel |
| API-006 | `PUT /process-instances/{key}/variables` `{variables, scopeKey?, local?}` | Set variables |
| API-007 | `POST /jobs/activate` | Long-poll job activation (Section 9) |
| API-008 | `POST /jobs/{key}/complete` · `/fail` · `/error` · `PATCH /jobs/{key}` (retries/timeout) | Job lifecycle |
| API-009 | `POST /messages` · `POST /signals` | Publish message/broadcast signal |
| API-010 | `GET /incidents` · `POST /incidents/{key}/resolve` | Incident ops |
| API-011 | `GET /user-tasks` · `POST /user-tasks/{key}/claim|unclaim|complete|assign` | User tasks (M5) |
| API-012 | `GET /process-instances/{key}/history` | Ordered element-instance & event history |
| API-013 | `GET /healthz` (liveness) · `GET /readyz` (DB reachable, migrations current) · `GET /metrics` (Prometheus) | Ops |

### 14.2 API rules
- **API-R1** Every mutating endpoint accepts optional header `Idempotency-Key`; same key + same body within 24 h returns the original response.
- **API-R2** List endpoints use cursor pagination (`pageSize` ≤ 1000, `after` cursor, response `{items, nextCursor}`) and filters (state, bpmnProcessId, time ranges, tenant, business key, variable equals — variable filter MAY be M7).
- **API-R3** `openapi.yaml` is generated/maintained alongside code; CI validates that handlers match the spec (contract tests).
- **API-R4** Standard error codes (minimum): `NOT_FOUND`, `INVALID_ARGUMENT`, `DEPLOYMENT_INVALID`, `INSTANCE_NOT_ACTIVE`, `JOB_NOT_ACTIVATED`, `MESSAGE_ALREADY_EXISTS`, `UNAUTHENTICATED`, `PERMISSION_DENIED`, `RESOURCE_EXHAUSTED`, `INTERNAL`.

### 14.3 Example: start instance

```http
POST /v1/process-instances
Content-Type: application/json

{ "bpmnProcessId": "order-process", "variables": { "order": { "id": "A-17", "total": 120.5 } } }
```
```json
{ "processInstanceKey": "2251799813685249", "processDefinitionKey": "2251799813685200", "version": 3, "bpmnProcessId": "order-process" }
```

### 14.4 Example: worker loop

```http
POST /v1/jobs/activate
{ "type": "charge-card", "worker": "billing-1", "timeoutMs": 30000, "maxJobs": 10, "requestTimeoutMs": 20000, "fetchVariables": ["order"] }
```
→ `{ "jobs": [ { "key":"…", "type":"charge-card", "processInstanceKey":"…", "elementId":"Task_Charge", "retries":3, "deadline":"…", "variables":{"order":{…}}, "customHeaders":{} } ] }`


