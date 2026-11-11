CREATE TABLE IF NOT EXISTS sessions(game_id TEXT PRIMARY KEY, admission_version INTEGER NOT NULL CHECK(admission_version>0), is_open BOOLEAN NOT NULL);
CREATE TABLE IF NOT EXISTS receipts(id TEXT PRIMARY KEY, blank_id TEXT UNIQUE NOT NULL, game_id TEXT NOT NULL,
team_id TEXT NOT NULL, task_id TEXT NOT NULL, task_number TEXT NOT NULL, admission_version INTEGER NOT NULL,
image_ref TEXT NOT NULL, accepted_at TEXT NOT NULL, object_key TEXT NOT NULL UNIQUE,
media_type TEXT NOT NULL, image_size BIGINT NOT NULL CHECK(image_size>0), image_sha256 TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS receipts_game ON receipts(game_id);
CREATE TABLE IF NOT EXISTS submissions(receipt_id TEXT PRIMARY KEY, blank_id TEXT UNIQUE NOT NULL, game_id TEXT NOT NULL,
team_id TEXT NOT NULL, task_id TEXT NOT NULL, task_number TEXT NOT NULL, verified_image BYTEA NOT NULL,
media_type TEXT NOT NULL, scope TEXT NOT NULL, operation_key TEXT NOT NULL, fingerprint TEXT NOT NULL,
worker_token TEXT NOT NULL, lease_until TEXT NOT NULL, UNIQUE(scope,operation_key));
CREATE INDEX IF NOT EXISTS submissions_lease ON submissions(lease_until);
CREATE TABLE IF NOT EXISTS operations(scope TEXT NOT NULL, operation_key TEXT NOT NULL, fingerprint TEXT NOT NULL,
result_json TEXT NOT NULL, PRIMARY KEY(scope,operation_key));
CREATE TABLE IF NOT EXISTS outbox(event_id TEXT PRIMARY KEY, event_json TEXT NOT NULL, available_at TEXT NOT NULL,
lease_until TEXT, worker_token TEXT, delivered INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS outbox_ready ON outbox(delivered,available_at,lease_until);
