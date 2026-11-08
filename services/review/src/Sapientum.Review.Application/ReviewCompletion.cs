using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
public sealed class ReviewCompletion(ITransactions transactions, IGameProfiles games, IClock clock)
{
    public async Task<CompletionAssessment> Get(string gameId, CancellationToken cancellationToken)
    {
        var game = await games.Get(gameId, cancellationToken);
        var now = await clock.Now(cancellationToken);
        return (await transactions.Run<CompletionAssessment>(null,
            s => new(Assess(s, game, now)), cancellationToken)).Value;
    }
    public async Task<Outcome<CompletionAssessment>> Receive(CommandIdentity identity, string gameId,
        int version, IReadOnlyList<string> receiptIds, CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<CompletionAssessment>(identity, cancellationToken);
        if (replay is not null) return replay;
        var game = await games.Get(gameId, cancellationToken);
        var now = await clock.Now(cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var completion = s.Completions.Get(gameId);
            completion.Receive(version, receiptIds, game.AdmissionVersion);
            s.Completions.Save(completion);
            return new Outcome<CompletionAssessment>(Assess(s, game, now));
        }, cancellationToken);
    }
    private static CompletionAssessment Assess(IReviewSession s, GameProfile game, DateTimeOffset now)
    {
        var completion = s.Completions.Get(game.Id);
        var checks = s.Checks.ForGame(game.Id);
        var appeals = s.Appeals.ForGame(game.Id);
        var assessment = completion.Assess(game, now, checks, appeals);
        if (completion.MarkPublished(assessment))
            s.Events.Add(new(Guid.NewGuid().ToString(), "AllChecksCompleted", now, game.Id,
                new CompletionEventData(game.Id, game.AdmissionVersion, completion.Snapshot().ReceiptIds!)));
        s.Completions.Save(completion);
        return assessment;
    }
}
