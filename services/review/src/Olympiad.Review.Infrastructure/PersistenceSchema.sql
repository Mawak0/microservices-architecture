CREATE TABLE IF NOT EXISTS checks (
id TEXT PRIMARY KEY, receipt_id TEXT NOT NULL UNIQUE, game_id TEXT NOT NULL,
snapshot TEXT NOT NULL, arrival_order BIGINT GENERATED ALWAYS AS IDENTITY
);
CREATE INDEX IF NOT EXISTS checks_game ON checks(game_id);
CREATE TABLE IF NOT EXISTS appeals (
id TEXT PRIMARY KEY, check_id TEXT NOT NULL REFERENCES checks(id),
game_id TEXT NOT NULL, team_id TEXT NOT NULL, task_id TEXT NOT NULL, snapshot TEXT NOT NULL, arrival_order BIGINT GENERATED ALWAYS AS IDENTITY,
UNIQUE(game_id,team_id,task_id)
);
CREATE TABLE IF NOT EXISTS quotas (
game_id TEXT PRIMARY KEY, limit_value BIGINT NOT NULL CHECK(limit_value>=0),
used INTEGER NOT NULL CHECK(used>=0)
);
CREATE TABLE IF NOT EXISTS completions (game_id TEXT PRIMARY KEY, snapshot TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS incoming_receipts (event_id TEXT PRIMARY KEY, receipt_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS idempotency (
actor_id TEXT NOT NULL, operation TEXT NOT NULL, key TEXT NOT NULL,
fingerprint TEXT NOT NULL, result TEXT NOT NULL, PRIMARY KEY(actor_id,operation,key)
);
CREATE TABLE IF NOT EXISTS outbox (
event_id TEXT PRIMARY KEY, message TEXT NOT NULL, published INTEGER NOT NULL DEFAULT 0, arrival_order BIGINT GENERATED ALWAYS AS IDENTITY
);

ALTER TABLE checks ADD COLUMN IF NOT EXISTS queue_stage INTEGER;
ALTER TABLE checks ADD COLUMN IF NOT EXISTS assigned_actor TEXT;
ALTER TABLE checks ADD COLUMN IF NOT EXISTS assignment_until TIMESTAMPTZ;
ALTER TABLE checks ADD COLUMN IF NOT EXISTS last_verdict_actor TEXT;
ALTER TABLE checks ADD COLUMN IF NOT EXISTS last_verdict_at TIMESTAMPTZ;
ALTER TABLE checks ADD COLUMN IF NOT EXISTS subject_team TEXT;
ALTER TABLE checks ADD COLUMN IF NOT EXISTS subject_task TEXT;
ALTER TABLE checks ADD COLUMN IF NOT EXISTS subject_blank TEXT;
UPDATE checks SET queue_stage=(snapshot::jsonb->>'stage')::INTEGER,
    assigned_actor=snapshot::jsonb #>> '{assignment,actorId}',
    assignment_until=(snapshot::jsonb #>> '{assignment,expiresAt}')::TIMESTAMPTZ,
    last_verdict_actor=snapshot::jsonb #>> '{history,-1,actorId}',
    last_verdict_at=(snapshot::jsonb #>> '{history,-1,recordedAt}')::TIMESTAMPTZ,
    subject_team=snapshot::jsonb #>> '{receipt,teamId}',
    subject_task=snapshot::jsonb #>> '{receipt,taskId}',
    subject_blank=snapshot::jsonb #>> '{receipt,blankId}'
WHERE queue_stage IS NULL;
ALTER TABLE checks ALTER COLUMN queue_stage SET NOT NULL;
CREATE INDEX IF NOT EXISTS checks_queue ON checks(game_id,queue_stage,arrival_order);
CREATE INDEX IF NOT EXISTS checks_assigned_actor ON checks(assigned_actor,assignment_until);
CREATE INDEX IF NOT EXISTS checks_recent_actor ON checks(game_id,last_verdict_actor,last_verdict_at DESC);
CREATE INDEX IF NOT EXISTS checks_team_task ON checks(game_id,subject_team,subject_task);
CREATE INDEX IF NOT EXISTS appeals_check ON appeals(check_id);
