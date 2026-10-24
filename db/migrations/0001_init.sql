-- 0001_init: core schema (spec section 15, FR-DB-001). Forward-only; never edit after release.
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
