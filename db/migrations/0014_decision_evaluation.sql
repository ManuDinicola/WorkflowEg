-- 0014_decision_evaluation: history of decision evaluations, from the API and from business rule tasks (FR-DMN-007).
CREATE TABLE decision_evaluation (
  key BIGINT PRIMARY KEY,
  tenant_id TEXT NOT NULL,
  decision_id TEXT NOT NULL,
  decision_key BIGINT,
  evaluated_at TIMESTAMPTZ NOT NULL,
  source TEXT NOT NULL,
  instance_key BIGINT,
  duration_ms INT NOT NULL,
  input JSONB NOT NULL,
  output JSONB,
  error TEXT
);
CREATE INDEX decision_evaluation_decision ON decision_evaluation (tenant_id, decision_id, key DESC);
CREATE INDEX decision_evaluation_instance ON decision_evaluation (tenant_id, instance_key) WHERE instance_key IS NOT NULL;
CREATE INDEX decision_evaluation_time ON decision_evaluation (evaluated_at);
