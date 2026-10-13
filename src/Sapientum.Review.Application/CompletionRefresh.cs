using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
public sealed record CompletionRefreshFailure(string GameId, string Code, FailureKind Kind);
public sealed record CompletionRefreshResult(int Refreshed, IReadOnlyList<CompletionRefreshFailure> Failures);
/// <summary>Refreshes persisted games after verdicts and deadline transitions, without a polling client.</summary>
public sealed class CompletionRefresh(ITransactions transactions, ReviewCompletion completion)
{
    public async Task<CompletionRefreshResult> Execute(CancellationToken cancellationToken)
    {
        var games = (await transactions.Run<IReadOnlyList<string>>(null,
            s => new(s.Completions.TrackedGames()), cancellationToken)).Value;
        var refreshed = 0;
        var failures = new List<CompletionRefreshFailure>();
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await completion.Get(game, cancellationToken); refreshed++; }
            catch (DomainFailure failure) when (failure.Kind is FailureKind.Dependency or FailureKind.Missing)
            {
                failures.Add(new(game, failure.Code, failure.Kind));
            }
        }
        return new(refreshed, failures.ToArray());
    }
}
