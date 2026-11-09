using System.Text.Json;
using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Infrastructure.Persistence;

internal sealed class OutboxRepository(SqliteScope db) : IOutboxRepository
{
    public Task AddAsync(IntakeEvent message, CancellationToken ct) => db.ExecuteAsync(
        "INSERT INTO outbox(event_id,event_json,available_at) VALUES($id,$json,$at)", ct,
        ("$id", message.EventId), ("$json", EventSerialization.Serialize(message)), ("$at", SqliteScope.Timestamp(message.OccurredAt)));

    public async Task<OutboxDelivery?> ClaimAsync(string workerToken, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct)
    {
        string? eventId = null, json = null;
        await using (var command = db.Command("""
            SELECT event_id,event_json FROM outbox WHERE delivered=0 AND available_at<=$now
                AND (lease_until IS NULL OR lease_until<=$now) ORDER BY available_at,event_id LIMIT 1
            """, ("$now", SqliteScope.Timestamp(now))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) { eventId = reader.GetString(0); json = reader.GetString(1); }
        if (eventId is null) return null;
        await db.ExecuteAsync("UPDATE outbox SET worker_token=$worker,lease_until=$lease WHERE event_id=$id", ct,
            ("$worker", workerToken), ("$lease", SqliteScope.Timestamp(leaseUntil)), ("$id", eventId));
        return new(EventSerialization.Deserialize(json!), workerToken);
    }
    public Task CompleteAsync(string eventId, string workerToken, CancellationToken ct) => db.ExecuteAsync(
        "UPDATE outbox SET delivered=1,worker_token=NULL,lease_until=NULL WHERE event_id=$id AND worker_token=$worker", ct,
        ("$id", eventId), ("$worker", workerToken));
    public Task RetryAsync(string eventId, string workerToken, DateTimeOffset retryAt, CancellationToken ct) => db.ExecuteAsync(
        "UPDATE outbox SET available_at=$retry,worker_token=NULL,lease_until=NULL WHERE event_id=$id AND worker_token=$worker", ct,
        ("$retry", SqliteScope.Timestamp(retryAt)), ("$id", eventId), ("$worker", workerToken));
}

public static class EventSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize(IntakeEvent message) => JsonSerializer.Serialize(message, message.GetType(), Options);
    public static IntakeEvent Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("type").GetString() switch
        {
            nameof(ReceiptAccepted) => JsonSerializer.Deserialize<ReceiptAccepted>(json, Options)!,
            nameof(IntakeClosed) => JsonSerializer.Deserialize<IntakeClosed>(json, Options)!,
            _ => throw new InvalidOperationException("Unsupported persisted event type.")
        };
    }
}
