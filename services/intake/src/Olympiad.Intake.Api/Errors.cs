using System.Text.Json;
using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Api;

internal static class Errors
{
    public static Task WriteAsync(HttpContext context, int status, string code, string message)
    { context.Response.StatusCode = status; return context.Response.WriteAsJsonAsync(new { code, message }, context.RequestAborted); }
    public static (int Status, string Code) Map(IntakeError error) => error switch
    {
        IntakeError.ValidationError => (422, "VALIDATION_ERROR"),
        IntakeError.InvalidImage => (422, "INVALID_IMAGE"),
        IntakeError.QrUnreadable => (422, "QR_UNREADABLE"),
        IntakeError.QrMismatch => (422, "QR_MISMATCH"),
        IntakeError.InvalidQr => (422, "INVALID_QR"),
        IntakeError.LegibilityNotConfirmed => (422, "LEGIBILITY_NOT_CONFIRMED"),
        IntakeError.BlankAlreadyAccepted => (409, "BLANK_ALREADY_ACCEPTED"),
        IntakeError.IntakeClosed => (409, "INTAKE_CLOSED"),
        IntakeError.TeamNotAdmitted => (409, "TEAM_NOT_ADMITTED"),
        IntakeError.IdempotencyKeyReused => (409, "IDEMPOTENCY_KEY_REUSED"),
        IntakeError.AdmissionVersionMismatch => (409, "ADMISSION_VERSION_MISMATCH"),
        IntakeError.DeadlineNotReached => (409, "DEADLINE_NOT_REACHED"),
        IntakeError.LateAdmissionActive => (409, "LATE_ADMISSION_ACTIVE"),
        IntakeError.NotFound => (404, "NOT_FOUND"),
        IntakeError.StorageUnavailable => (503, "STORAGE_UNAVAILABLE"),
        IntakeError.DependencyUnavailable => (503, "DEPENDENCY_UNAVAILABLE"),
        IntakeError.IntakeBusy => (503, "INTAKE_BUSY"),
        _ => throw new ArgumentOutOfRangeException(nameof(error))
    };
}

internal sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (IntakeException exception)
        { var (status, code) = Errors.Map(exception.Error); await Errors.WriteAsync(context, status, code, exception.Message); }
        catch (JsonException)
        { await Errors.WriteAsync(context, 422, "VALIDATION_ERROR", "JSON contains missing, unknown or invalid fields."); }
        catch (BadHttpRequestException)
        { await Errors.WriteAsync(context, 422, "VALIDATION_ERROR", "The request is malformed or exceeds the upload limit."); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException exception)
        { logger.LogWarning(exception, "Dependency request timed out."); await Errors.WriteAsync(context, 503, "DEPENDENCY_UNAVAILABLE", "A dependency timed out."); }
        catch (Exception exception)
        { logger.LogError(exception, "Intake operation could not complete."); await Errors.WriteAsync(context, 503, "STORAGE_UNAVAILABLE", "The operation could not be durably completed."); }
    }
}
