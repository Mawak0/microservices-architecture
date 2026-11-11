using Npgsql;
using Olympiad.Materials.Application;
using System.Text.Json;

namespace Olympiad.Materials.Infrastructure.Persistence;

internal sealed class PostgresIdempotencyRepository(NpgsqlConnection connection, NpgsqlTransaction transaction)
    : PostgresRepository(connection, transaction), IIdempotencyRepository
{
    public async Task<IdempotencyRecord?> FindAsync(string scope, string key, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT fingerprint,resource_ids_json FROM idempotency WHERE scope=@scope AND key=@key", ("@scope", scope), ("@key", key));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(scope, key, reader.GetString(0), JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? throw new InvalidDataException("Missing replay references."));
    }
    public async Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        await using var command = Command("INSERT INTO idempotency VALUES(@scope,@key,@fingerprint,@resources)", ("@scope", record.Scope), ("@key", record.Key),
            ("@fingerprint", record.Fingerprint), ("@resources", JsonSerializer.Serialize(record.ResourceIds)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
internal sealed class PostgresOutboxRepository(NpgsqlConnection connection, NpgsqlTransaction transaction)
    : PostgresRepository(connection, transaction), IOutboxRepository
{
    public async Task AddAsync(MaterialEvent message, CancellationToken cancellationToken)
    {
        await using var command = Command("INSERT INTO outbox(event_id,message_json) VALUES(@id,@json)", ("@id", message.EventId), ("@json", JsonSerializer.Serialize(message)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<MaterialEvent>> GetPendingAsync(int limit, CancellationToken cancellationToken)
    {
        var messages = new List<MaterialEvent>();
        await using var command = Command("SELECT message_json FROM outbox WHERE delivered=0 ORDER BY arrival_order LIMIT @limit", ("@limit", limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) messages.Add(JsonSerializer.Deserialize<MaterialEvent>(reader.GetString(0)) ?? throw new InvalidDataException("Missing event data."));
        return messages.AsReadOnly();
    }
    public async Task MarkDeliveredAsync(string eventId, CancellationToken cancellationToken)
    {
        await using var command = Command("UPDATE outbox SET delivered=1 WHERE event_id=@id", ("@id", eventId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
