namespace Olympiad.Intake.Domain;

public sealed class Receipt
{
    public string Id { get; }
    public BlankBinding Blank { get; }
    public int AdmissionVersion { get; }
    public string ImageRef { get; }
    public DateTimeOffset AcceptedAt { get; }

    private Receipt(string id, BlankBinding blank, int version, string imageRef, DateTimeOffset acceptedAt)
    {
        blank.RequireValid();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(imageRef) || version < 1)
            throw new IntakeException(IntakeError.ValidationError, "A durable image reference and receipt identity are required.");
        Id = id; Blank = blank; AdmissionVersion = version; ImageRef = imageRef; AcceptedAt = acceptedAt;
    }

    public static Receipt Accept(string id, BlankBinding blank, Admission admission, IntakeSession session,
        string imageRef, DateTimeOffset now)
    {
        session.RequireAccepting(admission, now);
        if (blank.GameId != admission.GameId)
            throw new IntakeException(IntakeError.ValidationError, "Blank and admission must belong to the same game.");
        return new(id, blank, admission.Version, imageRef, now);
    }

    public static Receipt Restore(string id, BlankBinding blank, int version, string imageRef, DateTimeOffset acceptedAt)
        => new(id, blank, version, imageRef, acceptedAt);
}
