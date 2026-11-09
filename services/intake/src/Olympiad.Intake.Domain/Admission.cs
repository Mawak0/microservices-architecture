namespace Olympiad.Intake.Domain;

public sealed record Admission(string GameId, int Version, string Status,
    DateTimeOffset SubmissionDeadline, bool LateSubmissionAllowed)
{
    public bool AllowsSubmission(DateTimeOffset now) => LateSubmissionAllowed ||
        (Status == "running" && now < SubmissionDeadline);

    public void RequireVersion(int requestedVersion)
    {
        if (requestedVersion != Version)
            throw new IntakeException(IntakeError.AdmissionVersionMismatch, "Admission permission has changed.");
    }

    public void RequireOpen(DateTimeOffset now)
    {
        if (!AllowsSubmission(now))
            throw new IntakeException(IntakeError.IntakeClosed, "Submission admission is closed.");
    }

    public void RequireClosable(DateTimeOffset now)
    {
        if (LateSubmissionAllowed)
            throw new IntakeException(IntakeError.LateAdmissionActive, "Disable late admission before closing intake.");
        if (now < SubmissionDeadline)
            throw new IntakeException(IntakeError.DeadlineNotReached, "The submission deadline has not expired.");
    }
}

public sealed record BlankBinding(string BlankId, string GameId, string TeamId,
    string TaskId, string TaskNumber)
{
    public void RequireValid()
    {
        if (new[] { BlankId, GameId, TeamId, TaskId, TaskNumber }.Any(string.IsNullOrWhiteSpace))
            throw new IntakeException(IntakeError.InvalidQr, "The blank registry returned incomplete identity.");
    }
}

public sealed record TeamAdmission(string TeamId, string GameId, bool Admitted)
{
    public void RequireEligible(BlankBinding blank)
    {
        if (!Admitted || TeamId != blank.TeamId || GameId != blank.GameId)
            throw new IntakeException(IntakeError.TeamNotAdmitted, "The team is not admitted to this game.");
    }
}
