using Olympiad.Review.Application;
namespace Olympiad.Review.Infrastructure;
/// <summary>At-least-once publication, with the exact committed event ID on every attempt.</summary>
public sealed class OutboxDispatcher(SqliteDatabase database, IEventPublisher publisher) : IOutboxDelivery
{
    public async Task<int> Drain(CancellationToken cancellationToken)
    {
        var pending = new List<ReviewEvent>();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT message FROM outbox WHERE published=0 ORDER BY rowid LIMIT 100";
            using var reader = command.ExecuteReader();
            while (reader.Read()) pending.Add(EventSerialization.Deserialize(reader.GetString(0)));
        }
        var published = 0;
        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await publisher.Publish(message, cancellationToken);
            using var connection = database.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE outbox SET published=1 WHERE event_id=$id";
            command.Parameters.AddWithValue("$id", message.EventId); command.ExecuteNonQuery(); published++;
        }
        return published;
    }
}
