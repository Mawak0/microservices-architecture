using Olympiad.Review.Domain;
namespace Olympiad.Review.Application;
public sealed class RecordVerdict(ITransactions transactions, IClock clock)
{
    public async Task<Outcome<CheckView>> Execute(CommandIdentity identity, string id, string token,
        Verdict verdict, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<CheckView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var check = s.Check(id); check.Record(verdict, identity.ActorId, token, now);
            s.Checks.Save(check); OutgoingEvents.Verdict(s.Events, check, "VerdictIssued", now);
            return new Outcome<CheckView>(CheckView.From(check));
        }, cancellationToken);
    }
}
public sealed class CorrectVerdict(ITransactions transactions, IClock clock)
{
    public async Task<Outcome<CheckView>> Execute(CommandIdentity identity, string id,
        Verdict verdict, string reason, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<CheckView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var check = s.Check(id); check.Correct(verdict, identity.ActorId, now, reason);
            s.Checks.Save(check); OutgoingEvents.Verdict(s.Events, check, "VerdictCorrected", now);
            return new Outcome<CheckView>(CheckView.From(check));
        }, cancellationToken);
    }
}
