namespace Olympiad.Intake.Domain;

public sealed class IntakeSession
{
    public string GameId { get; }
    public int AdmissionVersion { get; private set; }
    public bool IsOpen { get; private set; }

    private IntakeSession(string gameId, int admissionVersion, bool isOpen)
    {
        if (string.IsNullOrWhiteSpace(gameId) || admissionVersion < 1)
            throw new IntakeException(IntakeError.ValidationError, "A game and positive admission version are required.");
        GameId = gameId; AdmissionVersion = admissionVersion; IsOpen = isOpen;
    }

    public static IntakeSession Restore(string gameId, int version, bool isOpen) => new(gameId, version, isOpen);

    public static IntakeSession Open(Admission admission, int requestedVersion, DateTimeOffset now)
    {
        admission.RequireVersion(requestedVersion); admission.RequireOpen(now);
        return new(admission.GameId, admission.Version, true);
    }

    public void Synchronize(Admission admission, int requestedVersion, DateTimeOffset now)
    {
        RequireSameGame(admission); admission.RequireVersion(requestedVersion); admission.RequireOpen(now);
        if (admission.Version < AdmissionVersion)
            throw new IntakeException(IntakeError.AdmissionVersionMismatch, "A stale version cannot replace newer admission.");
        AdmissionVersion = admission.Version; IsOpen = true;
    }

    public void RequireAccepting(Admission admission, DateTimeOffset now)
    {
        RequireSameGame(admission); admission.RequireOpen(now);
        if (!IsOpen)
            throw new IntakeException(IntakeError.IntakeClosed, "Synchronize the reopened intake session before submitting.");
        if (admission.Version < AdmissionVersion)
            throw new IntakeException(IntakeError.AdmissionVersionMismatch, "Admission has become stale.");
    }

    public bool Close(Admission admission, int requestedVersion, DateTimeOffset now)
    {
        RequireSameGame(admission); admission.RequireVersion(requestedVersion); admission.RequireClosable(now);
        if (admission.Version < AdmissionVersion)
            throw new IntakeException(IntakeError.AdmissionVersionMismatch, "Admission has become stale.");
        var changed = IsOpen || AdmissionVersion != admission.Version;
        AdmissionVersion = admission.Version; IsOpen = false;
        return changed;
    }

    private void RequireSameGame(Admission admission)
    {
        if (admission.GameId != GameId)
            throw new IntakeException(IntakeError.ValidationError, "Admission belongs to a different game.");
    }
}
