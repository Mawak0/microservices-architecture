using Microsoft.Data.Sqlite;
using Olympiad.Intake.Application;

namespace Olympiad.Intake.Infrastructure.Persistence;

public sealed class SqliteDatabase(string connectionString) : IIntakeUnitOfWork
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.DataSource is not ("" or ":memory:"))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
            if (directory is not null) Directory.CreateDirectory(directory);
        }
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS sessions(game_id TEXT PRIMARY KEY, admission_version INTEGER NOT NULL CHECK(admission_version>0), is_open INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS receipts(id TEXT PRIMARY KEY, blank_id TEXT UNIQUE NOT NULL, game_id TEXT NOT NULL,
                team_id TEXT NOT NULL, task_id TEXT NOT NULL, task_number TEXT NOT NULL, admission_version INTEGER NOT NULL,
                image_ref TEXT NOT NULL, accepted_at TEXT NOT NULL, verified_image BLOB NOT NULL, media_type TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS receipts_game ON receipts(game_id);
            CREATE TABLE IF NOT EXISTS submissions(receipt_id TEXT PRIMARY KEY, blank_id TEXT UNIQUE NOT NULL, game_id TEXT NOT NULL,
                team_id TEXT NOT NULL, task_id TEXT NOT NULL, task_number TEXT NOT NULL, verified_image BLOB NOT NULL,
                media_type TEXT NOT NULL, scope TEXT NOT NULL, operation_key TEXT NOT NULL, fingerprint TEXT NOT NULL,
                worker_token TEXT NOT NULL, lease_until TEXT NOT NULL, UNIQUE(scope,operation_key));
            CREATE INDEX IF NOT EXISTS submissions_lease ON submissions(lease_until);
            CREATE TABLE IF NOT EXISTS operations(scope TEXT NOT NULL, operation_key TEXT NOT NULL, fingerprint TEXT NOT NULL,
                result_json TEXT NOT NULL, PRIMARY KEY(scope,operation_key));
            CREATE TABLE IF NOT EXISTS outbox(event_id TEXT PRIMARY KEY, event_json TEXT NOT NULL, available_at TEXT NOT NULL,
                lease_until TEXT, worker_token TEXT, delivered INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS outbox_ready ON outbox(delivered,available_at,lease_until);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<T> ExecuteAsync<T>(Func<IIntakeTransaction, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        var scope = new SqliteScope(connection, transaction);
        var result = await action(new Repositories(scope), ct);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(ct);
        return result;
    }

    private sealed class Repositories(SqliteScope scope) : IIntakeTransaction
    {
        public ISessionRepository Sessions { get; } = new SessionRepository(scope);
        public IReceiptRepository Receipts { get; } = new ReceiptRepository(scope);
        public ISubmissionRepository Submissions { get; } = new SubmissionRepository(scope);
        public IOperationRepository Operations { get; } = new OperationRepository(scope);
        public IOutboxRepository Outbox { get; } = new OutboxRepository(scope);
    }
}

internal sealed class SqliteScope(SqliteConnection connection, SqliteTransaction transaction)
{
    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(sql, parameters); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> ExistsAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(sql, parameters); return await command.ExecuteScalarAsync(ct) is not null;
    }
    public static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
}
