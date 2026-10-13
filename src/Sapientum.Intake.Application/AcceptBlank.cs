using Sapientum.Intake.Domain;

namespace Sapientum.Intake.Application;

public sealed record SubmitBlank(string QrToken, ReadOnlyMemory<byte> Image, string ImageMediaType, bool LegibilityConfirmed);
public sealed record AcceptedBlank(Receipt Receipt, bool Replayed);
public sealed record ImageLimits(int MaximumBytes = 15 * 1024 * 1024);

public sealed class AcceptBlank(IIntakeUnitOfWork store, IBlankRegistry registry,
    IAdmissionGateway games, ITeamAdmissionGateway teams, IImageQrDecoder decoder,
    SubmissionFinalizer finalizer, IClock clock, ImageLimits limits)
{

    public async Task<AcceptedBlank> ExecuteAsync(SubmitBlank command, OperationIdentity operation, CancellationToken ct)
    {
        var replay = await ReplayAsync(operation, ct);
        if (replay is not null) return new(replay, true);
        if (!command.LegibilityConfirmed)
            throw new IntakeException(IntakeError.LegibilityNotConfirmed, "The observer must confirm image legibility.");
        if (string.IsNullOrWhiteSpace(command.QrToken) || command.ImageMediaType is not ("image/png" or "image/jpeg"))
            throw new IntakeException(IntakeError.ValidationError, "A QR token and supported image media type are required.");
        var image = command.Image;
        if (image.Length == 0 || image.Length > limits.MaximumBytes)
            throw new IntakeException(IntakeError.InvalidImage, "Image exceeds the configured byte limit or is empty.");
        var scanned = decoder.Decode(image, command.ImageMediaType);
        if (!string.Equals(scanned, command.QrToken, StringComparison.Ordinal))
            throw new IntakeException(IntakeError.QrMismatch, "The image QR does not match the submitted token.");
        var blank = await registry.VerifyAsync(scanned, ct); blank.RequireValid();
        var admission = await games.GetAsync(blank.GameId, ct);
        (await teams.GetAsync(blank.TeamId, ct)).RequireEligible(blank);
        var now = await clock.NowAsync(ct);
        admission.RequireOpen(now);
        var pending = new PendingSubmission(Guid.NewGuid().ToString(), blank, image, command.ImageMediaType,
            operation, Guid.NewGuid().ToString(), now.AddSeconds(30));
        for (var attempt = 0; ; attempt++)
        {
            var result = await store.ExecuteAsync(async (tx, token) =>
            {
                var prior = await tx.Operations.FindAsync(operation, token);
                if (prior is not null)
                    return new ReservationResult(await tx.Receipts.FindAsync(prior.Match(operation).ReceiptId!, token), false);
                var priorPending = await tx.Submissions.FindForOperationAsync(operation, token);
                if (priorPending is not null)
                {
                    if (priorPending.Operation.Fingerprint != operation.Fingerprint)
                        throw new IntakeException(IntakeError.IdempotencyKeyReused, "The key belongs to a different pending request.");
                    return new ReservationResult(null, true);
                }
                var session = await tx.Sessions.FindAsync(blank.GameId, token)
                    ?? throw new IntakeException(IntakeError.IntakeClosed, "Intake has not been opened.");
                session.RequireAccepting(admission, now);
                if (await tx.Receipts.ExistsForBlankAsync(blank.BlankId, token) || await tx.Submissions.ExistsForBlankAsync(blank.BlankId, token))
                    throw new IntakeException(IntakeError.BlankAlreadyAccepted, "The blank is already accepted or reserved by an upload.");
                await tx.Submissions.AddAsync(pending, token);
                return new ReservationResult(null, false);
            }, ct);
            if (result.Receipt is not null) return new(result.Receipt, true);
            if (result.WaitForOwner)
            {
                if (attempt >= 400) throw new IntakeException(IntakeError.IntakeBusy, "The original upload is still pending; retry with the same key.");
                await Task.Delay(50, ct); continue;
            }
            break;
        }
        try { return new(await finalizer.ExecuteAsync(pending, ct), false); }
        catch (IntakeException exception) when (exception.Error is IntakeError.StorageUnavailable or IntakeError.IntakeClosed or IntakeError.TeamNotAdmitted)
        {
            // Definitive rejection or failed storage frees the blank. Cancellation and transient failures retain recovery bytes.
            await store.ExecuteAsync(async (tx, token) => { await tx.Submissions.RemoveAsync(pending.ReceiptId, pending.WorkerToken, token); return true; }, CancellationToken.None);
            throw;
        }
    }

    private Task<Receipt?> ReplayAsync(OperationIdentity operation, CancellationToken ct) => store.ExecuteAsync(async (tx, token) =>
    {
        var prior = await tx.Operations.FindAsync(operation, token);
        return prior is null ? null : await tx.Receipts.FindAsync(prior.Match(operation).ReceiptId!, token);
    }, ct);
    private sealed record ReservationResult(Receipt? Receipt, bool WaitForOwner);
}
