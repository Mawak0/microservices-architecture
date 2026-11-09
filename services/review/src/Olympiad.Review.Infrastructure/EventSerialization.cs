using System.Text.Json;
using Olympiad.Review.Application;
namespace Olympiad.Review.Infrastructure;
internal static class EventSerialization
{
    public static string Serialize(ReviewEvent message) => JsonSerializer.Serialize(new
    {
        message.EventId,
        message.Type,
        message.OccurredAt,
        message.AggregateId,
        Data = JsonSerializer.SerializeToElement(message.Data, message.Data.GetType(), DatabaseScope.Json)
    }, DatabaseScope.Json);
    public static ReviewEvent Deserialize(string value)
    {
        using var document = JsonDocument.Parse(value); var root = document.RootElement;
        var type = root.GetProperty("type").GetString()!;
        var dataType = type switch
        {
            "VerdictIssued" or "VerdictCorrected" => typeof(VerdictEventData),
            "AppealOpened" or "AppealResolved" => typeof(AppealEventData),
            "AppealUsageReturned" => typeof(ReturnedUsageData),
            "AllChecksCompleted" => typeof(CompletionEventData),
            _ => throw new InvalidOperationException("Unknown outbox event type.")
        };
        return new(root.GetProperty("eventId").GetString()!, type,
            root.GetProperty("occurredAt").GetDateTimeOffset(), root.GetProperty("aggregateId").GetString()!,
            (EventData)root.GetProperty("data").Deserialize(dataType, DatabaseScope.Json)!);
    }
}
internal sealed class EventOutbox(DatabaseScope database) : IEventOutbox
{
    public void Add(ReviewEvent message) => database.Execute(
        "INSERT INTO outbox(event_id,message) VALUES($id,$message)",
        ("$id", message.EventId), ("$message", EventSerialization.Serialize(message)));
}
