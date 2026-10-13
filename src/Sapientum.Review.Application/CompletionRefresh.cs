using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
/// <summary>Refreshes persisted games after verdicts and deadline transitions, without a polling client.</summary>
public sealed class CompletionRefresh(ITransactions transactions, ReviewCompletion completion)
{
    public async Task<int> Execute(CancellationToken cancellationToken)
    {
        var games = (await transactions.Run<IReadOnlyList<string>>(null,
            s => new(s.Completions.TrackedGames()), cancellationToken)).Value;
        var refreshed = 0;
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await completion.Get(game, cancellationToken); refreshed++; }
            catch (DomainFailure failure) when (failure.Kind == FailureKind.Dependency) { }
        }
        return refreshed;
    }
}
