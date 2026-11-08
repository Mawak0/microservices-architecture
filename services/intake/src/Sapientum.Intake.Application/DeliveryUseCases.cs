using Sapientum.Intake.Domain;

namespace Sapientum.Intake.Application;

public sealed class SubmissionFinalizer(IIntakeUnitOfWork store, IImageStorage images,
    IAdmissionGateway games, ITeamAdmissionGateway teams, IClock clock)
{
    public async Task<Receipt> ExecuteAsync(PendingSubmission pending, CancellationToken ct)
    {
        var imageRef = await images.SaveAsync(pending.ReceiptId, pending.Blank.BlankId, pending.Image, pending.MediaType, ct);
        // Acceptance is timed at final confirmation, after durable upload. Permission and team admission are refreshed.
        var admission = await games.GetAsync(pending.Blank.GameId, ct);
        (await teams.GetAsync(pending.Blank.TeamId, ct)).RequireEligible(pending.Blank);
        var now = await clock.NowAsync(ct);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var existing = await tx.Receipts.FindAsync(pending.ReceiptId, token);
            if (existing is not null) return existing;
            if (!await tx.Submissions.IsOwnedAsync(pending.ReceiptId, pending.WorkerToken, token))
                throw new IntakeException(IntakeError.IntakeBusy, "Upload reservation ownership changed.");
            var session = await tx.Sessions.FindAsync(pending.Blank.GameId, token)
                ?? throw new IntakeException(IntakeError.IntakeClosed, "Intake is not open.");
            var receipt = Receipt.Accept(pending.ReceiptId, pending.Blank, admission, session, imageRef, now);
            session.Synchronize(admission, admission.Version, now);
            await tx.Sessions.SaveAsync(session, token);
            await tx.Receipts.AddAsync(receipt, pending.Image, pending.MediaType, token);
            await tx.Outbox.AddAsync(ReceiptAccepted.From(receipt, Guid.NewGuid().ToString()), token);
            await tx.Operations.SaveAsync(pending.Operation, new(ReceiptId: receipt.Id), token);
            await tx.Submissions.RemoveAsync(pending.ReceiptId, pending.WorkerToken, token);
            return receipt;
        }, ct);
    }
}

