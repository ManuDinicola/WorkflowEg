-- 0003_timers_messages: timer leasing, tenant on timers and the message correlation marks (FR-TMR-002, FR-MSG-002).
ALTER TABLE timer ADD COLUMN tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE timer ADD COLUMN lease_until TIMESTAMPTZ;
CREATE INDEX timer_instance ON timer (instance_key) WHERE instance_key IS NOT NULL;
CREATE INDEX message_subscription_instance ON message_subscription (instance_key) WHERE instance_key IS NOT NULL;

-- A buffered message reaches a process definition at most once.
CREATE TABLE message_correlation (
  message_key BIGINT NOT NULL,
  definition_key BIGINT NOT NULL,
  PRIMARY KEY (message_key, definition_key)
);
