-- 0012_app_user: built-in users with passwords, for installations without an external identity provider (FR-SEC-011).
CREATE TABLE app_user (
  username TEXT PRIMARY KEY,
  password_hash TEXT NOT NULL,
  roles TEXT NOT NULL,
  tenant_id TEXT NOT NULL DEFAULT '',
  groups TEXT NOT NULL DEFAULT '',
  created_at TIMESTAMPTZ NOT NULL,
  disabled_at TIMESTAMPTZ,
  failed_attempts INT NOT NULL DEFAULT 0,
  locked_until TIMESTAMPTZ
);
