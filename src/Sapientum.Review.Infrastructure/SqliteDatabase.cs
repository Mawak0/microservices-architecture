using Microsoft.Data.Sqlite;
namespace Sapientum.Review.Infrastructure;
public sealed class SqliteDatabase
{
    private readonly string connectionString;
    public SqliteDatabase(string connectionString)
    {
        this.connectionString = connectionString;
        var settings = new SqliteConnectionStringBuilder(connectionString);
        if (settings.DataSource != ":memory:" && settings.DataSource.Length > 0)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(settings.DataSource));
            if (directory is not null) Directory.CreateDirectory(directory);
        }
    }
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery(); return connection;
    }
    public void Initialize()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS checks (
                id TEXT PRIMARY KEY, receipt_id TEXT NOT NULL UNIQUE, game_id TEXT NOT NULL,
                snapshot TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS checks_game ON checks(game_id);
            CREATE TABLE IF NOT EXISTS appeals (
                id TEXT PRIMARY KEY, check_id TEXT NOT NULL REFERENCES checks(id),
                game_id TEXT NOT NULL, team_id TEXT NOT NULL, task_id TEXT NOT NULL, snapshot TEXT NOT NULL,
                UNIQUE(game_id,team_id,task_id)
            );
            CREATE TABLE IF NOT EXISTS quotas (
                game_id TEXT PRIMARY KEY, limit_value INTEGER NOT NULL CHECK(limit_value>=0),
                used INTEGER NOT NULL CHECK(used>=0)
            );
            CREATE TABLE IF NOT EXISTS completions (game_id TEXT PRIMARY KEY, snapshot TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS incoming_receipts (event_id TEXT PRIMARY KEY, receipt_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS idempotency (
                actor_id TEXT NOT NULL, operation TEXT NOT NULL, key TEXT NOT NULL,
                fingerprint TEXT NOT NULL, result TEXT NOT NULL, PRIMARY KEY(actor_id,operation,key)
            );
            CREATE TABLE IF NOT EXISTS outbox (
                event_id TEXT PRIMARY KEY, message TEXT NOT NULL, published INTEGER NOT NULL DEFAULT 0
            );
            """;
        command.ExecuteNonQuery();
    }
}
