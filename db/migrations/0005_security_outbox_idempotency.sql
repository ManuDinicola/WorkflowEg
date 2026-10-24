-- 0005_security_outbox_idempotency: API keys (FR-SEC-001), outbox delivery bookkeeping (FR-HIS-002), idempotency scope (API-R1).
CREATE TABLE api_key (
  key_hash BYTEA PRIMARY KEY,
  name TEXT NOT NULL,
  roles TEXT NOT NULL,
  tenant_id TEXT NOT NULL DEFAULT '',
  created_at TIMESTAMPTZ NOT NULL,
  revoked_at TIMESTAMPTZ
);

ALTER TABLE outbox ADD COLUMN attempts INT NOT NULL DEFAULT 0;
ALTER TABLE outbox ADD COLUMN last_error TEXT;
ALTER TABLE outbox ADD COLUMN tenant_id TEXT NOT NULL DEFAULT '';
CREATE INDEX outbox_published ON outbox (published_at) WHERE published_at IS NOT NULL;
CREATE INDEX idempotency_key_created ON idempotency_key (created_at);
