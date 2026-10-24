-- 0009: user task forms (FR-UT-005). Same versioning rule as decisions: unchanged content keeps the version.
CREATE TABLE form_definition (
  key BIGINT PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  form_id TEXT NOT NULL,
  version INT NOT NULL,
  resource_name TEXT NOT NULL,
  schema JSONB NOT NULL,
  checksum BYTEA NOT NULL,
  deployed_at TIMESTAMPTZ NOT NULL,
  UNIQUE (tenant_id, form_id, version)
);
