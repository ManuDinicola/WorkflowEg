-- 0008: audit log of mutating API calls (FR-SEC-005).
CREATE TABLE audit_log (
  seq BIGSERIAL PRIMARY KEY,
  occurred_at TIMESTAMPTZ NOT NULL,
  tenant_id TEXT NOT NULL,
  actor TEXT NOT NULL,
  method TEXT NOT NULL,
  path TEXT NOT NULL,
  status INT NOT NULL
);
CREATE INDEX audit_log_tenant ON audit_log (tenant_id, seq);
