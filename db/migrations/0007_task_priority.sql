-- 0007: user task priority and follow-up date (FR-UT-004).
ALTER TABLE user_task ADD COLUMN follow_up_date TIMESTAMPTZ;
ALTER TABLE user_task ADD COLUMN priority INT NOT NULL DEFAULT 50;
CREATE INDEX user_task_priority ON user_task (tenant_id, state, priority DESC, key);
