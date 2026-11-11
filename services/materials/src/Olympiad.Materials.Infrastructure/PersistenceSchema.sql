CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY);
INSERT INTO schema_version VALUES (1) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS service_secrets (name TEXT PRIMARY KEY, value BYTEA NOT NULL);
CREATE TABLE IF NOT EXISTS blank_sequence (id INTEGER PRIMARY KEY CHECK(id=1), value BIGINT NOT NULL);
INSERT INTO blank_sequence VALUES (1,0) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS blanks (
id TEXT PRIMARY KEY, number BIGINT NOT NULL UNIQUE CHECK(number>0), game_id TEXT NOT NULL,
team_id TEXT NOT NULL, task_id TEXT NOT NULL, task_number TEXT NOT NULL,
qr_token TEXT NOT NULL UNIQUE, modifier_kind TEXT, modifier_value TEXT);
CREATE TABLE IF NOT EXISTS kits (
id TEXT PRIMARY KEY, game_id TEXT NOT NULL, team_id TEXT NOT NULL,
answer_blank_count INTEGER NOT NULL, task_card_count INTEGER NOT NULL,
task_cards_shared BOOLEAN NOT NULL, template_key TEXT NOT NULL, UNIQUE(game_id,team_id));
CREATE TABLE IF NOT EXISTS kit_blanks (
kit_id TEXT NOT NULL REFERENCES kits(id), blank_id TEXT NOT NULL REFERENCES blanks(id),
ordinal INTEGER NOT NULL, PRIMARY KEY(kit_id,blank_id), UNIQUE(kit_id,ordinal));
CREATE TABLE IF NOT EXISTS print_jobs (
id TEXT PRIMARY KEY, game_id TEXT NOT NULL, team_id TEXT, snapshot_json TEXT NOT NULL,
status TEXT NOT NULL, artifact_url TEXT, error_code TEXT, error_message TEXT,
processing_token TEXT, processing_expires_at TEXT, created_order INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS print_jobs_status ON print_jobs(status,created_order);
CREATE TABLE IF NOT EXISTS pdf_artifacts (
print_job_id TEXT PRIMARY KEY REFERENCES print_jobs(id), content BYTEA NOT NULL);
CREATE TABLE IF NOT EXISTS idempotency (
scope TEXT NOT NULL, key TEXT NOT NULL, fingerprint TEXT NOT NULL, resource_ids_json TEXT NOT NULL,
PRIMARY KEY(scope,key));
CREATE TABLE IF NOT EXISTS outbox (
event_id TEXT PRIMARY KEY, message_json TEXT NOT NULL, delivered INTEGER NOT NULL DEFAULT 0, arrival_order BIGINT GENERATED ALWAYS AS IDENTITY);
CREATE INDEX IF NOT EXISTS outbox_pending ON outbox(delivered);
