-- 0002_execution: columns the interpreter needs beyond the core schema (section 15).
ALTER TABLE element_instance ADD COLUMN incoming_flow_id TEXT;
ALTER TABLE job ADD COLUMN element_id TEXT NOT NULL DEFAULT '';
ALTER TABLE job ADD COLUMN result_variables JSONB;
CREATE INDEX job_instance ON job (instance_key);
CREATE INDEX incident_key_instance ON incident (key, instance_key);
