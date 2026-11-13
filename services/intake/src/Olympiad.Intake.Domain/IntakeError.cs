namespace Olympiad.Intake.Domain;

public enum IntakeError
{
    ValidationError, InvalidImage, QrUnreadable, QrMismatch, InvalidQr,
    LegibilityNotConfirmed, BlankAlreadyAccepted, IntakeClosed,
    TeamNotAdmitted, StorageUnavailable, DependencyUnavailable,
    IdempotencyKeyReused, AdmissionVersionMismatch, DeadlineNotReached,
    LateAdmissionActive, NotFound, IntakeBusy
}

public sealed class IntakeException(IntakeError error, string message) : Exception(message)
{
    public IntakeError Error { get; } = error;
    public string? CompensationResourceId { get; init; }
}
