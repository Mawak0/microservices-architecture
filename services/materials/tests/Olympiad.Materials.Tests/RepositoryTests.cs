using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;
using Olympiad.Materials.Infrastructure.Persistence;
using Olympiad.Materials.Infrastructure.Security;
using Npgsql;
using Xunit;

namespace Olympiad.Materials.Tests;

internal sealed class RepositoryFixture : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"materials-unit-{Guid.NewGuid()}");
    public string ConnectionString => TestDatabase.For(Path.Combine(directory, "test.db"));
    public async Task<IMaterialsTransactions> CreateAsync(bool postgres)
    {
        if (!postgres) return new MemoryTransactions();
        var database = new PostgresDatabase(ConnectionString); await database.InitializeAsync(default); return new PostgresMaterialsTransactions(database);
    }
    public ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        return ValueTask.CompletedTask;
    }
}
public sealed class RepositoryTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RepositoryContractRoundTripsIdentityAndTransactionalMetadata(bool postgres)
    {
        await using var fixture = new RepositoryFixture(); var store = await fixture.CreateAsync(postgres);
        await store.ExecuteAsync(async (s, ct) =>
        {
            Assert.Null(await s.Blanks.FindAsync("unknown", ct));
            var number = await s.Blanks.ReserveNumbersAsync(2, ct); Assert.Equal(1, number);
            await s.Blanks.AddAsync(Sample.Blank(number: number), ct);
            await s.Idempotency.AddAsync(new("scope", "key", "hash", ["blank"]), ct);
            await s.Outbox.AddAsync(new("event", "BlanksIssued", DateTimeOffset.UtcNow, "blank", "game", "team", BlankIds: ["blank"]), ct);
            return true;
        }, default);
        await store.ExecuteAsync(async (s, ct) =>
        {
            Assert.Equal("blank", (await s.Blanks.FindByTokenAsync("qr", ct))?.Id);
            Assert.Equal("task-1", (await s.Blanks.FindAsync("blank", ct))?.TaskId);
            Assert.Equal("hash", (await s.Idempotency.FindAsync("scope", "key", ct))?.Fingerprint);
            Assert.Single(await s.Outbox.GetPendingAsync(10, ct)); await s.Outbox.MarkDeliveredAsync("event", ct);
            Assert.Empty(await s.Outbox.GetPendingAsync(10, ct)); Assert.Equal(3, await s.Blanks.ReserveNumbersAsync(1, ct));
            return true;
        }, default);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RepositoryContractRollsBackAggregateAndOutboxTogether(bool postgres)
    {
        await using var fixture = new RepositoryFixture(); var store = await fixture.CreateAsync(postgres);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteAsync<bool>(async (s, ct) =>
        {
            await s.Blanks.AddAsync(Sample.Blank(), ct);
            await s.Outbox.AddAsync(new("event", "KitPrepared", DateTimeOffset.UtcNow, "kit", "game", "team", KitId: "kit"), ct);
            throw new InvalidOperationException("Simulated failure before commit");
        }, default));
        await store.ExecuteAsync(async (s, ct) => { Assert.Null(await s.Blanks.FindAsync("blank", ct)); Assert.Empty(await s.Outbox.GetPendingAsync(10, ct)); return true; }, default);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RepositoryContractPersistsKitAndSnapshotAndCompletedArtifact(bool postgres)
    {
        await using var fixture = new RepositoryFixture(); var store = await fixture.CreateAsync(postgres);
        var job = new PrintJob("job", "game", "team", Sample.Snapshot()); var now = DateTimeOffset.UtcNow;
        await store.ExecuteAsync(async (s, ct) =>
        {
            await s.Blanks.AddAsync(Sample.Blank(), ct); await s.Kits.AddAsync(new("kit", "game", "team", 1, 2, false, "layout/v1", ["blank"]), ct);
            await s.PrintJobs.AddAsync(job, ct); return true;
        }, default);
        await store.ExecuteAsync(async (s, ct) =>
        {
            Assert.Equal("kit", (await s.Kits.FindForTeamAsync("game", "team", ct))?.Id);
            var pending = await s.PrintJobs.FindNextPendingAsync(now, ct); Assert.NotNull(pending);
            Assert.Equal("Математическая олимпиада", pending.Snapshot.Parameters["heading"].Display);
            Assert.True(pending.TryStart("token", now, TimeSpan.FromMinutes(1))); await s.PrintJobs.SaveAsync(pending, ct);
            pending.Complete("token", "/artifact"); await s.PrintJobs.StoreArtifactAsync(pending.Id, new("object", "application/pdf", 3, "hash"), ct); await s.PrintJobs.SaveAsync(pending, ct); return true;
        }, default);
        await store.ExecuteAsync(async (s, ct) =>
        {
            Assert.Null(await s.PrintJobs.FindNextPendingAsync(now.AddHours(1), ct));
            Assert.Equal(PrintStatus.Completed, (await s.PrintJobs.FindAsync("job", ct))?.Status);
            Assert.Equal(new PrintArtifact("object", "application/pdf", 3, "hash"), await s.PrintJobs.FindArtifactAsync("job", ct)); return true;
        }, default);
    }
    [Fact] public async Task FreshDatabaseAdapterRetainsSecretBlanksReplayAndPendingEvents()
    {
        await using var fixture = new RepositoryFixture(); var first = await fixture.CreateAsync(true);
        var database = new PostgresDatabase(fixture.ConnectionString); var key = await database.GetOrCreateSigningKeyAsync(null, default);
        var dependency = new StubDependencies();
        var issued = await new IssueBlanks(dependency, dependency, first, new HmacBlankTokenProtector(key), dependency)
            .ExecuteAsync(Sample.Admin(), new("game", "team", "task-1", 1, null), default);
        var reopened = await fixture.CreateAsync(true); var restoredKey = await new PostgresDatabase(fixture.ConnectionString).GetOrCreateSigningKeyAsync(null, default);
        Assert.Equal(key, restoredKey); dependency.Unavailable = true;
        var repeat = await new IssueBlanks(dependency, dependency, reopened, new HmacBlankTokenProtector(restoredKey), dependency)
            .ExecuteAsync(Sample.Admin(), new("game", "team", "task-1", 1, null), default);
        Assert.Equal(issued[0].QrToken, repeat[0].QrToken);
        Assert.Single(await reopened.ExecuteAsync((s, ct) => s.Outbox.GetPendingAsync(100, ct), default));
    }
    [Fact] public async Task PostgresConcurrentIssuanceUsesUniqueSequenceAndOnlyOneReplayEffect()
    {
        await using var fixture = new RepositoryFixture(); var store = await fixture.CreateAsync(true); var dependency = new StubDependencies();
        var handler = new IssueBlanks(dependency, dependency, store, new HmacBlankTokenProtector(new byte[32]), dependency);
        var issued = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => handler.ExecuteAsync(Sample.Admin($"key-{i}"), new("game", "team", "task-1", 2, null), default)));
        Assert.Equal(10, issued.SelectMany(b => b).Select(b => b.Number).Distinct().Count());
        var repeats = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => handler.ExecuteAsync(Sample.Admin("same"), new("game", "team", "task-1", 2, null), default)));
        Assert.Equal(2, repeats.SelectMany(b => b).Select(b => b.Id).Distinct().Count());
        Assert.Equal(6, (await store.ExecuteAsync((s, ct) => s.Outbox.GetPendingAsync(100, ct), default)).Count);
    }
}
