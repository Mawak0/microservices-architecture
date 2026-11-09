namespace Olympiad.Review.Application;
public sealed class CheckQueries(ITransactions transactions, IClock clock)
{
    public async Task<CheckView> Get(string id, CancellationToken cancellationToken)
    {
        var now = await clock.Now(cancellationToken);
        return (await transactions.Run<CheckView>(null, s =>
        {
            var check = s.Check(id); if (check.Expire(now)) s.Checks.Save(check);
            return new(CheckView.From(check));
        }, cancellationToken)).Value;
    }
    public async Task<IReadOnlyList<CheckView>> List(string gameId, CancellationToken cancellationToken)
    {
        var now = await clock.Now(cancellationToken);
        return (await transactions.Run<IReadOnlyList<CheckView>>(null, s =>
        {
            var checks = s.Checks.ForGame(gameId);
            foreach (var check in checks) if (check.Expire(now)) s.Checks.Save(check);
            return new(checks.Select(CheckView.From).ToArray());
        }, cancellationToken)).Value;
    }
}
