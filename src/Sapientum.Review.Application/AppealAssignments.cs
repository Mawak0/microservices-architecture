using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
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
            foreach (var appeal in s.Appeals.ForGame(gameId).Where(a => !a.Resolved))
            {
                var check = s.Check(appeal.CheckId);
                if (check.Expire(now)) s.Checks.Save(check);
                if (check.Stage != CheckStage.AppealPending) continue;
                check.Claim(identity.ActorId, Guid.NewGuid().ToString(), now, settings.Duration, true);
                s.Checks.Save(check); return new(AppealView.From(appeal, check));
            }
            return new(null);
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
        CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<AppealView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var appeal = s.Appeal(id); var check = s.Check(appeal.CheckId);
            if (appeal.Resolved) throw new DomainFailure("APPEAL_ALREADY_RESOLVED");
            check.Release(identity.ActorId, token, now);
            s.Checks.Save(check); return new Outcome<AppealView>(AppealView.From(appeal, check));
        }, cancellationToken);
    }
}
