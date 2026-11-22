using Olympiad.Review.Domain;
namespace Olympiad.Review.Application;
public sealed class AppealAssignments(ITransactions transactions, IClock clock, LeaseSettings settings)
{
    public async Task<AppealView> Get(string id, CancellationToken cancellationToken)
    {
        var now = await clock.Now(cancellationToken);
        return (await transactions.Run<AppealView>(null, s =>
        {
            var appeal = s.Appeal(id); var check = s.Check(appeal.CheckId);
            if (check.Expire(now)) s.Checks.Save(check);
            return new(AppealView.From(appeal, check));
        }, cancellationToken)).Value;
    }
    public async Task<Outcome<AppealView>> Claim(CommandIdentity identity, string id, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<AppealView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var appeal = s.Appeal(id); if (appeal.Resolved) throw new DomainFailure("APPEAL_ALREADY_RESOLVED");
            var check = s.Check(appeal.CheckId);
            AssignmentCapacity.Require(s.Checks, identity.ActorId, now);
            check.Claim(identity.ActorId, Guid.NewGuid().ToString(), now, settings.Duration, true);
            s.Checks.Save(check); return new Outcome<AppealView>(AppealView.From(appeal, check));
        }, cancellationToken);
    }
    public async Task<Outcome<AppealView?>> Next(CommandIdentity identity, string gameId, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<AppealView?>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run<AppealView?>(identity, s =>
        {
            if (AssignmentCapacity.IsFull(s.Checks, identity.ActorId, now)) return new(null);
            var check = s.Checks.NextAvailable(gameId, now, appeal: true);
            if (check is null) return new(null);
            var appeal = s.Appeals.FindByCheck(check.Id) ?? throw new InvalidOperationException("Appeal case has no appeal.");
            if (appeal.Resolved) throw new InvalidOperationException("Resolved appeal in the active queue.");
            check.Claim(identity.ActorId, Guid.NewGuid().ToString(), now, settings.Duration, true);
            s.Checks.Save(check);
            return new(AppealView.From(appeal, check));
        }, cancellationToken);
    }
    public async Task<Outcome<AppealView>> Renew(CommandIdentity identity, string id, string token,
        CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<AppealView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var appeal = s.Appeal(id); var check = s.Check(appeal.CheckId);
            if (appeal.Resolved) throw new DomainFailure("APPEAL_ALREADY_RESOLVED");
            check.Renew(identity.ActorId, token, now, settings.Duration);
            s.Checks.Save(check); return new Outcome<AppealView>(AppealView.From(appeal, check));
        }, cancellationToken);
    }
    public async Task<Outcome<AppealView>> Release(CommandIdentity identity, string id, string token,
        bool mayReleaseOtherOwner, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<AppealView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var appeal = s.Appeal(id); var check = s.Check(appeal.CheckId);
            if (appeal.Resolved) throw new DomainFailure("APPEAL_ALREADY_RESOLVED");
            check.Release(identity.ActorId, token, now, mayReleaseOtherOwner);
            s.Checks.Save(check); return new Outcome<AppealView>(AppealView.From(appeal, check));
        }, cancellationToken);
    }
}
