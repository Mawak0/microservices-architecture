using Microsoft.Data.Sqlite;
using Olympiad.Materials.Application;

namespace Olympiad.Materials.Infrastructure.Persistence;

public sealed class SqliteDatabase
{
    private readonly string connectionString;
    public SqliteDatabase(string connectionString)
    {
        var configuredPooling = new SqliteConnectionStringBuilder(connectionString).Pooling;
        var builder = new SqliteConnectionStringBuilder(connectionString) { DefaultTimeout = 30, Pooling = configuredPooling, ForeignKeys = true };
        if (builder.DataSource != ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (directory is not null) Directory.CreateDirectory(directory);
        }
        this.connectionString = builder.ToString();
    }
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY);
            INSERT OR IGNORE INTO schema_version VALUES (1);
            CREATE TABLE IF NOT EXISTS service_secrets (name TEXT PRIMARY KEY, value BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS blank_sequence (id INTEGER PRIMARY KEY CHECK(id=1), value INTEGER NOT NULL);
            INSERT OR IGNORE INTO blank_sequence VALUES (1,0);
            CREATE TABLE IF NOT EXISTS blanks (
              id TEXT PRIMARY KEY, number INTEGER NOT NULL UNIQUE CHECK(number>0), game_id TEXT NOT NULL,
              team_id TEXT NOT NULL, task_id TEXT NOT NULL, task_number TEXT NOT NULL,
              qr_token TEXT NOT NULL UNIQUE, modifier_kind TEXT, modifier_value TEXT);
            CREATE TABLE IF NOT EXISTS kits (
              id TEXT PRIMARY KEY, game_id TEXT NOT NULL, team_id TEXT NOT NULL,
              answer_blank_count INTEGER NOT NULL, task_card_count INTEGER NOT NULL,
              task_cards_shared INTEGER NOT NULL, template_key TEXT NOT NULL, UNIQUE(game_id,team_id));
            CREATE TABLE IF NOT EXISTS kit_blanks (
              kit_id TEXT NOT NULL REFERENCES kits(id), blank_id TEXT NOT NULL REFERENCES blanks(id),
              ordinal INTEGER NOT NULL, PRIMARY KEY(kit_id,blank_id), UNIQUE(kit_id,ordinal));
            CREATE TABLE IF NOT EXISTS print_jobs (
              id TEXT PRIMARY KEY, game_id TEXT NOT NULL, team_id TEXT, snapshot_json TEXT NOT NULL,
              status TEXT NOT NULL, artifact_url TEXT, error_code TEXT, error_message TEXT,
              processing_token TEXT, processing_expires_at TEXT, created_order INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS print_jobs_status ON print_jobs(status,created_order);
            CREATE TABLE IF NOT EXISTS pdf_artifacts (
              print_job_id TEXT PRIMARY KEY REFERENCES print_jobs(id), content BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS idempotency (
              scope TEXT NOT NULL, key TEXT NOT NULL, fingerprint TEXT NOT NULL, resource_ids_json TEXT NOT NULL,
              PRIMARY KEY(scope,key));
            CREATE TABLE IF NOT EXISTS outbox (
              event_id TEXT PRIMARY KEY, message_json TEXT NOT NULL, delivered INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS outbox_pending ON outbox(delivered);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task<byte[]> GetOrCreateSigningKeyAsync(byte[]? configured, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT value FROM service_secrets WHERE name='qr-signing-v1'";
        var prior = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        if (prior is not null)
        {
            if (configured is not null && !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(configured, prior))
                throw new InvalidOperationException("The configured QR key differs from the key protecting persisted blanks. Rotate keys with an explicit migration.");
            return prior;
        }
        var key = configured ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        if (key.Length < 32) throw new InvalidOperationException("Security:QrSigningKeyBase64 must contain at least 32 random bytes.");
        command.CommandText = "INSERT INTO service_secrets(name,value) VALUES('qr-signing-v1',$value)";
        command.Parameters.AddWithValue("$value", key); await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken); return key;
    }
}

public sealed class SqliteMaterialsTransactions(SqliteDatabase database) : IMaterialsTransactions
{
    public async Task<T> ExecuteAsync<T>(Func<IMaterialsSession, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var result = await action(new SqliteSession(connection, transaction), cancellationToken);
        await transaction.CommitAsync(cancellationToken); return result;
    }
}

internal sealed class SqliteSession : IMaterialsSession
{
    public IBlankRepository Blanks { get; }
    public IKitRepository Kits { get; }
    public IPrintJobRepository PrintJobs { get; }
    public IIdempotencyRepository Idempotency { get; }
    public IOutboxRepository Outbox { get; }
    public SqliteSession(SqliteConnection connection, SqliteTransaction transaction)
    {
        Blanks = new SqliteBlankRepository(connection, transaction); Kits = new SqliteKitRepository(connection, transaction);
        PrintJobs = new SqlitePrintJobRepository(connection, transaction); Idempotency = new SqliteIdempotencyRepository(connection, transaction);
        Outbox = new SqliteOutboxRepository(connection, transaction);
    }
}

internal abstract class SqliteRepository(SqliteConnection connection, SqliteTransaction transaction)
{
    protected SqliteCommand Command(string sql, params (string Key, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    protected static string? NullableText(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
}
