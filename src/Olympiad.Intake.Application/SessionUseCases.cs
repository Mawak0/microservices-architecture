using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Application;

public sealed class SynchronizeIntake(IIntakeUnitOfWork store, IAdmissionGateway games, IClock clock)
{
    public async Task<SessionSnapshot> ExecuteAsync(string gameId, int version, OperationIdentity operation, CancellationToken ct)
    {
        var saved = await store.ExecuteAsync(async (tx, token) => await tx.Operations.FindAsync(operation, token), ct);
        if (saved is not null) return saved.Match(operation).Session!;
        var admission = await games.GetAsync(gameId, ct);
        var now = await clock.NowAsync(ct);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var prior = await tx.Operations.FindAsync(operation, token);
            if (prior is not null) return prior.Match(operation).Session!;
            var session = await tx.Sessions.FindAsync(gameId, token);
            if (session is null) session = IntakeSession.Open(admission, version, now);
            else session.Synchronize(admission, version, now);
            await tx.Sessions.SaveAsync(session, token);
            var result = new SessionSnapshot(gameId, session.AdmissionVersion, "open", await tx.Receipts.ListIdsAsync(gameId, token));
            await tx.Operations.SaveAsync(operation, new(Session: result), token);
            return result;
        }, ct);
    }
}

public sealed class CloseIntake(IIntakeUnitOfWork store, IAdmissionGateway games, IClock clock)
{
    public async Task<SessionSnapshot> ExecuteAsync(string gameId, int version, OperationIdentity operation, CancellationToken ct)
    {
        var saved = await store.ExecuteAsync(async (tx, token) => await tx.Operations.FindAsync(operation, token), ct);
        if (saved is not null) return saved.Match(operation).Session!;
        var admission = await games.GetAsync(gameId, ct);
        var now = await clock.NowAsync(ct);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var prior = await tx.Operations.FindAsync(operation, token);
            if (prior is not null) return prior.Match(operation).Session!;
            var session = await tx.Sessions.FindAsync(gameId, token)
                ?? throw new IntakeException(IntakeError.NotFound, "Intake session was not found.");
            // A pending upload is not yet accepted. Closing excludes it; its finalizer must recheck the session.
            var changed = session.Close(admission, version, now);
            var ids = await tx.Receipts.ListIdsAsync(gameId, token);
            await tx.Sessions.SaveAsync(session, token);
            var result = new SessionSnapshot(gameId, session.AdmissionVersion, "closed", ids);
            if (changed)
                await tx.Outbox.AddAsync(new IntakeClosed(Guid.NewGuid().ToString(), now, gameId,
                    new(gameId, session.AdmissionVersion, ids)), token);
            await tx.Operations.SaveAsync(operation, new(Session: result), token);
            return result;
        }, ct);
    }
}

public sealed class GetIntakeSession(IIntakeUnitOfWork store)
{
    public Task<SessionSnapshot> ExecuteAsync(string gameId, CancellationToken ct) => store.ExecuteAsync(async (tx, token) =>
    {
        var session = await tx.Sessions.FindAsync(gameId, token)
            ?? throw new IntakeException(IntakeError.NotFound, "Intake session was not found.");
        return new SessionSnapshot(gameId, session.AdmissionVersion, session.IsOpen ? "open" : "closed",
            await tx.Receipts.ListIdsAsync(gameId, token));
    }, ct);
}

public sealed class GetReceipt(IIntakeUnitOfWork store)
{
    public Task<Receipt> ExecuteAsync(string receiptId, CancellationToken ct) => store.ExecuteAsync(async (tx, token) =>
        await tx.Receipts.FindAsync(receiptId, token)
            ?? throw new IntakeException(IntakeError.NotFound, "Receipt was not found."), ct);
}
