-- 0011_api_key_groups: groups of an API key; a key with groups sees and works on user tasks only when it is the assignee or a candidate (FR-SEC-009).
ALTER TABLE api_key ADD COLUMN groups TEXT NOT NULL DEFAULT '';
