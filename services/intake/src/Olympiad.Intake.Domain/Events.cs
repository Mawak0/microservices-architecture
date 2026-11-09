namespace Olympiad.Intake.Domain;

public abstract record IntakeEvent(string EventId, DateTimeOffset OccurredAt, string AggregateId)
{
    public abstract string Type { get; }
}

public sealed record ReceiptAcceptedData(string ReceiptId, string BlankId, string GameId,
    int AdmissionVersion, string TeamId, string TaskId, string TaskNumber, string ImageRef, DateTimeOffset AcceptedAt);

public sealed record ReceiptAccepted(string EventId, DateTimeOffset OccurredAt, string AggregateId,
    ReceiptAcceptedData Data) : IntakeEvent(EventId, OccurredAt, AggregateId)
{
    public override string Type => nameof(ReceiptAccepted);
    public static ReceiptAccepted From(Receipt receipt, string eventId) => new(eventId, receipt.AcceptedAt, receipt.Id,
        new(receipt.Id, receipt.Blank.BlankId, receipt.Blank.GameId, receipt.AdmissionVersion,
            receipt.Blank.TeamId, receipt.Blank.TaskId, receipt.Blank.TaskNumber, receipt.ImageRef, receipt.AcceptedAt));
}

public sealed record IntakeClosedData(string GameId, int AdmissionVersion, IReadOnlyList<string> ReceiptIds);
public sealed record IntakeClosed(string EventId, DateTimeOffset OccurredAt, string AggregateId,
    IntakeClosedData Data) : IntakeEvent(EventId, OccurredAt, AggregateId)
{
    public override string Type => nameof(IntakeClosed);
}
