-- DMN decisions (FR-DMN-003): one row per decision and version, same versioning rule as process definitions.
CREATE TABLE decision_definition (
  key BIGINT PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  decision_id TEXT NOT NULL,
  version INT NOT NULL,
  resource_name TEXT NOT NULL,
  resource BYTEA NOT NULL,
  checksum BYTEA NOT NULL,
  deployed_at TIMESTAMPTZ NOT NULL,
  UNIQUE (tenant_id, decision_id, version)
);
