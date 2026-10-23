## 15. Persistence schema (PostgreSQL, normative core)

Use these tables (names fixed; add indexes as stated). All `key` columns are `BIGINT`.

```sql
CREATE TABLE process_definition (
  key BIGINT PRIMARY KEY, bpmn_process_id TEXT NOT NULL, version INT NOT NULL,
  resource_name TEXT NOT NULL, resource BYTEA NOT NULL, checksum BYTEA NOT NULL,
  tenant_id TEXT NOT NULL DEFAULT '', deployed_at TIMESTAMPTZ NOT NULL,
  UNIQUE (tenant_id, bpmn_process_id, version)
);
CREATE INDEX ON process_definition (tenant_id, bpmn_process_id, version DESC);

CREATE TABLE process_instance (
  key BIGINT PRIMARY KEY, definition_key BIGINT NOT NULL REFERENCES process_definition(key),
  parent_instance_key BIGINT, parent_element_instance_key BIGINT,
  state TEXT NOT NULL CHECK (state IN ('ACTIVE','COMPLETED','TERMINATED')),
  business_key TEXT, tenant_id TEXT NOT NULL DEFAULT '',
  started_at TIMESTAMPTZ NOT NULL, ended_at TIMESTAMPTZ, row_version BIGINT NOT NULL DEFAULT 0
);
CREATE INDEX ON process_instance (state, started_at);
CREATE INDEX ON process_instance (business_key) WHERE business_key IS NOT NULL;
CREATE INDEX ON process_instance (parent_instance_key) WHERE parent_instance_key IS NOT NULL;

CREATE TABLE element_instance (
  key BIGINT PRIMARY KEY, instance_key BIGINT NOT NULL REFERENCES process_instance(key),
  scope_key BIGINT NOT NULL, element_id TEXT NOT NULL, element_type TEXT NOT NULL,
  state TEXT NOT NULL, loop_counter INT, started_at TIMESTAMPTZ NOT NULL, ended_at TIMESTAMPTZ
);
CREATE INDEX ON element_instance (instance_key, state);
CREATE INDEX ON element_instance (scope_key);

CREATE TABLE variable (
  instance_key BIGINT NOT NULL, scope_key BIGINT NOT NULL, name TEXT NOT NULL,
  value JSONB NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (scope_key, name)
);
CREATE INDEX ON variable (instance_key);

CREATE TABLE job (
  key BIGINT PRIMARY KEY, instance_key BIGINT NOT NULL, element_instance_key BIGINT NOT NULL,
  type TEXT NOT NULL, state TEXT NOT NULL, retries INT NOT NULL,
  retry_backoff_until TIMESTAMPTZ, deadline TIMESTAMPTZ, worker TEXT,
  custom_headers JSONB NOT NULL DEFAULT '{}', variables JSONB NOT NULL DEFAULT '{}',
  error_message TEXT, tenant_id TEXT NOT NULL DEFAULT '', created_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX job_activatable ON job (tenant_id, type, created_at) WHERE state = 'ACTIVATABLE';
CREATE INDEX job_deadline ON job (deadline) WHERE state = 'ACTIVATED';

CREATE TABLE timer (
  key BIGINT PRIMARY KEY, instance_key BIGINT, element_instance_key BIGINT,
  definition_key BIGINT, element_id TEXT NOT NULL, kind TEXT NOT NULL,
  due_at TIMESTAMPTZ NOT NULL, repetitions INT, interval_iso TEXT, state TEXT NOT NULL DEFAULT 'ACTIVE'
);
CREATE INDEX timer_due ON timer (due_at) WHERE state = 'ACTIVE';

CREATE TABLE message_subscription (
  key BIGINT PRIMARY KEY, instance_key BIGINT, element_instance_key BIGINT,
  definition_key BIGINT, element_id TEXT NOT NULL, kind TEXT NOT NULL,
  message_name TEXT NOT NULL, correlation_key TEXT NOT NULL, tenant_id TEXT NOT NULL DEFAULT ''
);
CREATE INDEX ON message_subscription (tenant_id, message_name, correlation_key);

CREATE TABLE buffered_message (
  key BIGINT PRIMARY KEY, name TEXT NOT NULL, correlation_key TEXT NOT NULL,
  message_id TEXT, variables JSONB NOT NULL DEFAULT '{}', ttl_until TIMESTAMPTZ NOT NULL,
  tenant_id TEXT NOT NULL DEFAULT '', created_at TIMESTAMPTZ NOT NULL
);
CREATE UNIQUE INDEX ON buffered_message (tenant_id, name, correlation_key, message_id) WHERE message_id IS NOT NULL;
CREATE INDEX ON buffered_message (ttl_until);

CREATE TABLE incident (
  key BIGINT PRIMARY KEY, instance_key BIGINT NOT NULL, element_instance_key BIGINT, job_key BIGINT,
  error_type TEXT NOT NULL, error_message TEXT NOT NULL,
  state TEXT NOT NULL CHECK (state IN ('OPEN','RESOLVED')), created_at TIMESTAMPTZ NOT NULL, resolved_at TIMESTAMPTZ
);
CREATE INDEX ON incident (instance_key, state);

CREATE TABLE user_task (
  key BIGINT PRIMARY KEY, instance_key BIGINT NOT NULL, element_instance_key BIGINT NOT NULL,
  assignee TEXT, candidate_groups TEXT[] NOT NULL DEFAULT '{}', candidate_users TEXT[] NOT NULL DEFAULT '{}',
  due_date TIMESTAMPTZ, form_key TEXT, state TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE history_event (            -- append-only audit trail
  seq BIGSERIAL PRIMARY KEY, instance_key BIGINT NOT NULL, element_instance_key BIGINT,
  event_type TEXT NOT NULL, element_id TEXT, payload JSONB NOT NULL DEFAULT '{}', occurred_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ON history_event (instance_key, seq);

CREATE TABLE outbox (
  seq BIGSERIAL PRIMARY KEY, topic TEXT NOT NULL, payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL, published_at TIMESTAMPTZ
);
CREATE INDEX ON outbox (seq) WHERE published_at IS NULL;

CREATE TABLE idempotency_key (
  key TEXT PRIMARY KEY, request_hash BYTEA NOT NULL, response JSONB NOT NULL, created_at TIMESTAMPTZ NOT NULL
);
```

- **FR-DB-001** Migrations are forward-only, numbered, and applied by `flowd migrate`; `serve` refuses to start if migrations are behind (unless `--auto-migrate`).
- **FR-DB-002** Archival: completed instances older than `retention` (default 30 d) are moved/deleted by a background janitor in batches; history retention configurable separately.


## 16. Concurrency & consistency

- **FR-CON-001** All state mutations for one PI are serialized by the row lock on `process_instance`. A command targeting a locked PI waits up to `lockTimeout` (5 s) then returns `503 RETRY_LATER`.
- **FR-CON-002** Cross-instance operations (message to many instances, signals, cascade cancel) MUST be split into one transaction per instance; the initiating call is idempotent and resumable (progress tracked via outbox/work items).
- **FR-CON-003** Deadlock avoidance: when a command must lock >1 PI (e.g., call activity parent/child), always lock in ascending `key` order.
- **FR-CON-004** Job activation MUST NOT lock process instances (only job rows).
- **FR-CON-005** `completeJob` locks the PI, then verifies job state; a stale worker (deadline passed and re-activated by another worker) receives `409 JOB_NOT_ACTIVATED`.


## 17. History, events, outbox

- **FR-HIS-001** Every element lifecycle transition, variable change, job event, incident event, timer fire, message correlation, and instance state change writes a `history_event` row in the same transaction (event types are an enum documented in `docs/events.md`).
- **FR-HIS-002** The outbox publisher (leased, `SKIP LOCKED`) delivers events to configured sinks: HTTP webhook (HMAC-signed), and optionally Kafka/NATS in a later milestone. Delivery is at-least-once; payloads include a unique `eventId` for consumer dedupe.
- **FR-HIS-003** `GET /process-instances/{key}/history` returns events ordered by `seq`.


