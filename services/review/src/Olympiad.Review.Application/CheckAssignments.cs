using Olympiad.Review.Domain;
namespace Olympiad.Review.Application;
public sealed class CheckAssignments(ITransactions transactions, IClock clock, LeaseSettings settings)
{
    public async Task<Outcome<CheckView>> Claim(CommandIdentity identity, string id, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<CheckView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var c = s.Check(id);
            AssignmentCapacity.Require(s.Checks, identity.ActorId, now);
            c.Claim(identity.ActorId, Guid.NewGuid().ToString(), now, settings.Duration, false);
            s.Checks.Save(c); return new Outcome<CheckView>(CheckView.From(c));
        }, cancellationToken);
    }
    public async Task<Outcome<CheckView?>> Next(CommandIdentity identity, string gameId, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<CheckView?>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run<CheckView?>(identity, s =>
        {
            if (AssignmentCapacity.IsFull(s.Checks, identity.ActorId, now)) return new(null);
            var next = s.Checks.NextAvailable(gameId, now, appeal: false);
            if (next is null) return new(null);
            next.Claim(identity.ActorId, Guid.NewGuid().ToString(), now, settings.Duration, false);
            s.Checks.Save(next); return new(CheckView.From(next));
        }, cancellationToken);
    }
    public async Task<Outcome<CheckView>> Renew(CommandIdentity identity, string id, string token,
        CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<CheckView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var c = s.Check(id); c.Renew(identity.ActorId, token, now, settings.Duration);
            s.Checks.Save(c); return new Outcome<CheckView>(CheckView.From(c));
        }, cancellationToken);
    }
    public async Task<Outcome<CheckView>> Release(CommandIdentity identity, string id, string token,
        bool mayReleaseOtherOwner, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<CheckView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var c = s.Check(id); c.Release(identity.ActorId, token, now, mayReleaseOtherOwner);
            s.Checks.Save(c); return new Outcome<CheckView>(CheckView.From(c));
        }, cancellationToken);
    }
}
