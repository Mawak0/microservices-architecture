namespace Olympiad.Review.Application;
public sealed class QuotaCommands(ITransactions transactions)
{
    public async Task<QuotaView> Get(string gameId, CancellationToken cancellationToken) =>
        (await transactions.Run<QuotaView>(null, s => new(QuotaView.From(s.Quotas.Get(gameId))), cancellationToken)).Value;
    public Task<Outcome<QuotaView>> Set(CommandIdentity identity, string gameId, int limit,
        CancellationToken cancellationToken) => transactions.Run(identity, s =>
        {
            var quota = s.Quotas.Get(gameId); quota.SetLimit(limit); s.Quotas.Save(quota);
            return new Outcome<QuotaView>(QuotaView.From(quota));
        }, cancellationToken);
}
