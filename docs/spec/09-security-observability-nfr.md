## 18. Security & multi-tenancy

- **FR-SEC-001** AuthN: JWT bearer (OIDC-compatible, JWKS validation) and static API keys (hashed in DB) for workers. AuthN MUST be on by default; an explicit `--insecure-no-auth` dev flag exists.
- **FR-SEC-002** AuthZ: roles `admin`, `operator` (incidents, cancel, retries), `developer` (deploy), `worker` (jobs only), `reader`; enforced per endpoint. Roles come from JWT claim (configurable).
- **FR-SEC-003 Tenancy.** `tenantId` from token claim; every query MUST be filtered by tenant. A cross-tenant access attempt returns `404`, not `403`. Single-tenant mode = tenant `''`.
- **FR-SEC-004** Input limits: request body ≤ 4 MiB; BPMN resource ≤ 2 MiB; rate limits per API key (token bucket, configurable).
- **FR-SEC-005** Secrets never logged; variable values are NOT logged at INFO level.
- **FR-SEC-006** XML parsing MUST disable external entities/DTDs (XXE-safe).


## 19. Observability

- **FR-OBS-001 Metrics (Prometheus):** `flowd_process_instances_started_total`, `…_completed_total`, `…_active`, `flowd_jobs_created_total{type}`, `flowd_jobs_activated_total{type}`, `flowd_job_activation_latency_seconds{type}`, `flowd_jobs_timed_out_total`, `flowd_incidents_open`, `flowd_timers_due_lag_seconds`, `flowd_command_duration_seconds{command}`, `flowd_command_lock_wait_seconds`, `flowd_outbox_pending`.
- **FR-OBS-002 Tracing:** one trace per command; W3C `traceparent` accepted and propagated; process instance key as span attribute.
- **FR-OBS-003 Logs:** JSON, fields `ts, level, msg, instanceKey, elementId, jobKey, traceId`.


## 20. Non-functional requirements

- **NFR-001 Throughput target (single node, 4 vCPU, local Postgres):** ≥ 300 process-instance starts/s and ≥ 1 000 job complete/s for a 5-task linear process. Provide `bench/` scripts and publish results.
- **NFR-002 Latency:** p99 command latency < 100 ms at 50 % of the above load.
- **NFR-003 Durability:** after an acknowledged response, state survives process crash (Postgres `synchronous_commit=on`). Chaos test: kill engine mid-command → no lost or double-applied transitions.
- **NFR-004 Scalability:** adding engine nodes increases throughput until DB-bound; tested with 3 nodes.
- **NFR-005 Recovery:** startup time < 5 s regardless of data volume (no in-memory rebuild).
- **NFR-006 Test coverage:** engine core ≥ 90 % line coverage; overall ≥ 80 %.
- **NFR-007 Compatibility:** Postgres 14–17 supported.
- **NFR-008 Documentation:** README quickstart (5 min to first running instance), `docs/` for architecture, BPMN support matrix, API, ops runbook.


