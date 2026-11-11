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
