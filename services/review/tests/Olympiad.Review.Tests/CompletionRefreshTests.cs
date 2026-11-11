using Microsoft.Data.Sqlite;
using Olympiad.Review.Application;
using Olympiad.Review.Domain;
using Olympiad.Review.Infrastructure;
using Xunit;
namespace Olympiad.Review.Tests;

public sealed class CompletionRefreshTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "olympiad-completion-tests-" + Guid.NewGuid());
    private readonly ITransactions transactions;
    public CompletionRefreshTests()
    {
        var database = new SqliteDatabase("Data Source=" + Path.Combine(directory, "review.db"));
        database.Initialize(); transactions = new SqliteTransactions(database);
    }
    [Theory]
    [InlineData(FailureKind.Missing)]
    [InlineData(FailureKind.Dependency)]
    public async Task ExpectedFailureOfFirstGameDoesNotBlockLaterGame(FailureKind kind)
    {
        await SeedGames();
        var completion = new ReviewCompletion(transactions, new FaultyFirstGame(kind), new FixedClock());
        var result = await new CompletionRefresh(transactions, completion).Execute(default);
        Assert.Equal(1, result.Refreshed);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("a-unavailable", failure.GameId); Assert.Equal(kind, failure.Kind);
        var published = (await transactions.Run<bool>(null,
            s => new(s.Completions.Get("b-available").Snapshot().Published), default)).Value;
        Assert.True(published);
    }
    [Fact]
    public async Task CancellationAndProgrammingErrorsAreNotConvertedToPerGameFailure()
    {
        await SeedGames();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var completion = new ReviewCompletion(transactions, new ProgrammingFailure(), new FixedClock());
        var refresh = new CompletionRefresh(transactions, completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.Execute(cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh.Execute(default));
    }
    private Task<Outcome<int>> SeedGames() => transactions.Run<int>(null, s =>
    {
        foreach (var id in new[] { "a-unavailable", "b-available" })
        {
            var completion = new GameReviewCompletion(id); completion.Receive(1, [], 1);
            s.Completions.Save(completion);
        }
        return new(0);
    }, default);
    private sealed class FaultyFirstGame(FailureKind kind) : IGameProfiles
    {
        public Task<GameProfile> Get(string id, CancellationToken cancellationToken)
        {
            if (id == "a-unavailable") throw new DomainFailure("EXTERNAL_GAME_UNAVAILABLE", kind);
            return Task.FromResult(DomainTests.Game() with { Id = id });
        }
    }
    private sealed class ProgrammingFailure : IGameProfiles
    {
        public Task<GameProfile> Get(string id, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Programming error must propagate.");
    }
    private sealed class FixedClock : IClock
    {
        public Task<DateTimeOffset> Now(CancellationToken cancellationToken) => Task.FromResult(DomainTests.Now);
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
