using System.Data;
using Npgsql;

namespace Olympiad.Persistence.Postgres;

/// <summary>Infrastructure-only local transactions. Callbacks must not perform external effects.</summary>
public sealed class PostgresStore
{
    private readonly string connectionString;
    public PostgresStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Storage:ConnectionString must identify a PostgreSQL database.", nameof(connectionString));
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
            throw new ArgumentException("A PostgreSQL host and database are required.", nameof(connectionString));
        this.connectionString = settings.ConnectionString;
    }
    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task InitializeAsync(string schema, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Serialize only schema creation, including simultaneous replicas starting for the first time.
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(current_database()), hashtext(current_schema()));", connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
    public async Task<T> RunAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var connection = await OpenAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var result = await action(connection, transaction, ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (PostgresException ex) when (Retryable(ex, attempt))
            { await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 20) * (attempt + 1)), ct); }
        }
    }
    public T Run<T>(Func<NpgsqlConnection, NpgsqlTransaction, T> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                var result = action(connection, transaction);
                ct.ThrowIfCancellationRequested(); transaction.Commit(); return result;
            }
            catch (PostgresException ex) when (Retryable(ex, attempt))
            { ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 20) * (attempt + 1))); }
        }
    }
    private static bool Retryable(PostgresException ex, int attempt) => attempt < 7 &&
        ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;
}
