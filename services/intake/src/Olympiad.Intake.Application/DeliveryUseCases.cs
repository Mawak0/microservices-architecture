using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Application;

public sealed class SubmissionFinalizer(IIntakeUnitOfWork store, IImageStorage images,
    IAdmissionGateway games, ITeamAdmissionGateway teams, IClock clock)
{
    public async Task<(Receipt? Accepted,bool Compensate)> AbandonAsync(PendingSubmission pending,CancellationToken ct)
    {
        var now=await clock.NowAsync(ct);
        return await store.ExecuteAsync<(Receipt?,bool)>(async (tx,token) =>
        {
            var accepted=await tx.Receipts.FindAsync(pending.ReceiptId,token);
            if(accepted is not null) return (accepted,false);
            if(!await tx.Submissions.IsOwnedAsync(pending.ReceiptId,pending.WorkerToken,token)) return (null,false);
            await tx.Outbox.AddAsync(new UploadAbandoned(Guid.NewGuid().ToString(),now,pending.ReceiptId),token);
            await tx.Submissions.RemoveAsync(pending.ReceiptId,pending.WorkerToken,token);
            return (null,true);
        },ct);
    }
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
            await tx.Outbox.AddAsync(ReceiptAccepted.From(receipt, Guid.NewGuid().ToString()) with
                {OperationId=pending.Operation.WorkflowId,ProcessVersion=pending.Operation.WorkflowVersion}, token);
            await tx.Operations.SaveAsync(pending.Operation, new(ReceiptId: receipt.Id), token);
            await tx.Submissions.RemoveAsync(pending.ReceiptId, pending.WorkerToken, token);
            return receipt;
        }, ct);
    }
}

public sealed class RecoverPendingSubmission(IIntakeUnitOfWork store, SubmissionFinalizer finalizer, IClock clock)
{
    public async Task<bool> ExecuteAsync(CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var pending = await store.ExecuteAsync((tx, token) =>
            tx.Submissions.ClaimExpiredAsync(Guid.NewGuid().ToString(), now, now.AddSeconds(30), token), ct);
        if (pending is null) return false;
        try { await finalizer.ExecuteAsync(pending, ct); }
        catch (IntakeException exception) when (exception.Error is IntakeError.IntakeClosed or IntakeError.TeamNotAdmitted)
        {
            await finalizer.AbandonAsync(pending,ct);
        }
        return true;
    }
}

public sealed class DeliverPendingEvent(IIntakeUnitOfWork store, IEventPublisher publisher, IClock clock)
{
    public async Task<bool> ExecuteAsync(CancellationToken ct)
    {
        var now = await clock.NowAsync(ct);
        var delivery = await store.ExecuteAsync((tx, token) =>
            tx.Outbox.ClaimAsync(Guid.NewGuid().ToString(), now, now.AddSeconds(30), token), ct);
        if (delivery is null) return false;
        try
        {
            await publisher.PublishAsync(delivery.Event, ct);
            await store.ExecuteAsync(async (tx, token) => { await tx.Outbox.CompleteAsync(delivery.Event.EventId, delivery.WorkerToken, token); return true; }, ct);
        }
        catch when (!ct.IsCancellationRequested)
        {
            await store.ExecuteAsync(async (tx, token) => { await tx.Outbox.RetryAsync(delivery.Event.EventId, delivery.WorkerToken, now.AddMilliseconds(250), token); return true; }, ct);
            throw;
        }
        return true;
    }
}
