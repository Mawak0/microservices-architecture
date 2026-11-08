using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
public sealed class CheckAssignments(ITransactions transactions, IClock clock, LeaseSettings settings)
{
    public async Task<Outcome<CheckView>> Claim(CommandIdentity identity, string id, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<CheckView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var c = s.Check(id); c.Claim(identity.ActorId, Guid.NewGuid().ToString(), now, settings.Duration, false);
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
            var checks = s.Checks.ForGame(gameId);
            foreach (var c in checks) if (c.Expire(now)) s.Checks.Save(c);
            var next = checks.FirstOrDefault(c => c.Stage == CheckStage.Pending);
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
