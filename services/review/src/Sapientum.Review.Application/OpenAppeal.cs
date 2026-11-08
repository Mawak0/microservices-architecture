using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
public sealed class OpenAppeal(ITransactions transactions, CheckQueries checks,
    IGameProfiles games, IClock clock, AppealEligibility eligibility)
{
    public async Task<Outcome<AppealView>> Execute(CommandIdentity identity, string checkId,
        string reason, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<AppealView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var check = await checks.Get(checkId, cancellationToken);
        var game = await games.Get(check.GameId, cancellationToken);
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var current = s.Check(checkId);
            eligibility.Require(game, current, now);
            if (s.Appeals.Exists(game.Id, current.Receipt.TeamId, current.Receipt.TaskId))
                throw new DomainFailure("APPEAL_ALREADY_USED");
            var quota = s.Quotas.Get(game.Id); quota.Use();
            current.OpenAppeal();
            var appeal = Appeal.Open(Guid.NewGuid().ToString(), current, reason);
            s.Checks.Save(current); s.Quotas.Save(quota); s.Appeals.Save(appeal);
            OutgoingEvents.Appeal(s.Events, appeal, current, "AppealOpened", now);
            return new Outcome<AppealView>(AppealView.From(appeal, current), true);
        }, cancellationToken);
    }
}
