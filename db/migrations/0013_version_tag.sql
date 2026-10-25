-- 0013_version_tag: optional version tag of a process definition, for call activities that bind to a tag (FR-DEP-007).
ALTER TABLE process_definition ADD COLUMN version_tag TEXT;
CREATE INDEX process_definition_version_tag ON process_definition (tenant_id, bpmn_process_id, version_tag) WHERE version_tag IS NOT NULL;
