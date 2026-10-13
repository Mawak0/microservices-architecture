using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
public sealed class ResolveAppeal(ITransactions transactions, IClock clock)
{
    public async Task<Outcome<AppealView>> Execute(CommandIdentity identity, string appealId, string token,
        string outcome, Verdict? verdict, string reason, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<AppealView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var appeal = s.Appeal(appealId);
            if (appeal.Resolved) throw new DomainFailure("APPEAL_ALREADY_RESOLVED");
            var check = s.Check(appeal.CheckId);
            check.ResolveAppeal(outcome == "jury-error-corrected", verdict, identity.ActorId, token, now, reason);
            var refund = appeal.Resolve(outcome);
            s.Checks.Save(check); s.Appeals.Save(appeal);
            OutgoingEvents.Appeal(s.Events, appeal, check, "AppealResolved", now);
            if (refund)
            {
                var quota = s.Quotas.Get(appeal.GameId); quota.Return(); s.Quotas.Save(quota);
                OutgoingEvents.Verdict(s.Events, check, "VerdictCorrected", now);
                s.Events.Add(new(Guid.NewGuid().ToString(), "AppealUsageReturned", now, appeal.Id,
                    new ReturnedUsageData(appeal.GameId, check.Receipt.AdmissionVersion, appeal.Id)));
            }
            return new Outcome<AppealView>(AppealView.From(appeal, check));
        }, cancellationToken);
    }
}
