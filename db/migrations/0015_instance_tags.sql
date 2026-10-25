-- 0015_instance_tags: tags set when an instance is started, comma separated (FR-EXEC-020).
ALTER TABLE process_instance ADD COLUMN tags TEXT NOT NULL DEFAULT '';
