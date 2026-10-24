-- 0004_signals_user_tasks: signal subscriptions share the subscription table; user tasks get the columns the engine needs.
ALTER TABLE message_subscription ADD COLUMN signal BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE user_task ADD COLUMN element_id TEXT NOT NULL DEFAULT '';
ALTER TABLE user_task ADD COLUMN tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE user_task ADD COLUMN result_variables JSONB;
CREATE INDEX user_task_instance ON user_task (instance_key);
CREATE INDEX user_task_open ON user_task (tenant_id, state, key);
