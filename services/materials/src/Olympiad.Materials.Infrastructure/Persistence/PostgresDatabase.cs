using Olympiad.Persistence.Postgres;
using Npgsql;
using Olympiad.Materials.Application;

namespace Olympiad.Materials.Infrastructure.Persistence;

public sealed class PostgresDatabase
{
    private static string Schema { get { using var stream = typeof(PostgresDatabase).Assembly.GetManifestResourceStream("Olympiad.Materials.Infrastructure.PersistenceSchema.sql")!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); } }

    private readonly PostgresStore store;
    public PostgresDatabase(string connectionString) => store = new(connectionString);
    public Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => store.OpenAsync(ct);

    internal PostgresStore Store => store;
    public Task InitializeAsync(CancellationToken ct = default) => store.InitializeAsync(Schema, ct);

    public async Task<byte[]> GetOrCreateSigningKeyAsync(byte[]? configured, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(184912)";
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        command.CommandText = "INSERT INTO service_secrets(name,value) VALUES('qr-signing-v1',@value)";
        command.Parameters.AddWithValue("@value", key); await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken); return key;
    }
}

public sealed class PostgresMaterialsTransactions(PostgresDatabase database) : IMaterialsTransactions
{
    public Task<T> ExecuteAsync<T>(Func<IMaterialsSession, CancellationToken, Task<T>> action, CancellationToken ct) =>
        database.Store.RunAsync((connection, transaction, token) => action(new PostgresSession(connection, transaction), token), ct);
}

internal sealed class PostgresSession : IMaterialsSession
{
    public IBlankRepository Blanks { get; }
    public IKitRepository Kits { get; }
    public IPrintJobRepository PrintJobs { get; }
    public IIdempotencyRepository Idempotency { get; }
    public IOutboxRepository Outbox { get; }
    public PostgresSession(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Blanks = new PostgresBlankRepository(connection, transaction); Kits = new PostgresKitRepository(connection, transaction);
        PrintJobs = new PostgresPrintJobRepository(connection, transaction); Idempotency = new PostgresIdempotencyRepository(connection, transaction);
        Outbox = new PostgresOutboxRepository(connection, transaction);
    }
}

internal abstract class PostgresRepository(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    protected NpgsqlCommand Command(string sql, params (string Key, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    protected static string? NullableText(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
}
