-- 0010: an API key can be limited to a list of process ids (FR-SEC-006). Empty means all processes.
ALTER TABLE api_key ADD COLUMN processes TEXT NOT NULL DEFAULT '';
