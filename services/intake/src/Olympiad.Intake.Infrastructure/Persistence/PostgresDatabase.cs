using Olympiad.Persistence.Postgres;
using Npgsql;
using Olympiad.Intake.Application;

namespace Olympiad.Intake.Infrastructure.Persistence;

public sealed class PostgresDatabase : IIntakeUnitOfWork
{
    private static string Schema { get { using var stream = typeof(PostgresDatabase).Assembly.GetManifestResourceStream("Olympiad.Intake.Infrastructure.PersistenceSchema.sql")!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); } }

    private readonly PostgresStore store;
    public PostgresDatabase(string connectionString) => store = new(connectionString);
    public Task InitializeAsync(CancellationToken ct = default) => store.InitializeAsync(Schema, ct);

    public Task<T> ExecuteAsync<T>(Func<IIntakeTransaction, CancellationToken, Task<T>> action, CancellationToken ct) =>
        store.RunAsync((connection, transaction, token) => action(new Repositories(new PostgresScope(connection, transaction)), token), ct);

    private sealed class Repositories(PostgresScope scope) : IIntakeTransaction
    {
        public ISessionRepository Sessions { get; } = new SessionRepository(scope);
        public IReceiptRepository Receipts { get; } = new ReceiptRepository(scope);
        public ISubmissionRepository Submissions { get; } = new SubmissionRepository(scope);
        public IOperationRepository Operations { get; } = new OperationRepository(scope);
        public IOutboxRepository Outbox { get; } = new OutboxRepository(scope);
    }
}

internal sealed class PostgresScope(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    public NpgsqlCommand Command(string sql, params (string Name, object? Value)[] parameters)
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
