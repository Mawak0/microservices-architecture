using Sapientum.Materials.Application;
using Sapientum.Materials.Domain;
using Sapientum.Materials.Infrastructure.Persistence;
using Sapientum.Materials.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Sapientum.Materials.Tests;

internal sealed class RepositoryFixture : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"materials-unit-{Guid.NewGuid()}");
    public string ConnectionString => $"Data Source={Path.Combine(directory, "test.db")}";
    public async Task<IMaterialsTransactions> CreateAsync(bool sqlite)
    {
        if (!sqlite) return new MemoryTransactions();
        var database = new SqliteDatabase(ConnectionString); await database.InitializeAsync(default); return new SqliteMaterialsTransactions(database);
    }
    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder(ConnectionString)
            { DefaultTimeout = 30, Pooling = true, ForeignKeys = true }.ToString()));
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        return ValueTask.CompletedTask;
    }
}
public sealed class RepositoryTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RepositoryContractRoundTripsIdentityAndTransactionalMetadata(bool sqlite)
    {
        await using var fixture = new RepositoryFixture(); var store = await fixture.CreateAsync(sqlite);
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
    public async Task RepositoryContractRollsBackAggregateAndOutboxTogether(bool sqlite)
    {
        await using var fixture = new RepositoryFixture(); var store = await fixture.CreateAsync(sqlite);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteAsync<bool>(async (s, ct) =>
        {
            await s.Blanks.AddAsync(Sample.Blank(), ct);
            await s.Outbox.AddAsync(new("event", "KitPrepared", DateTimeOffset.UtcNow, "kit", "game", "team", KitId: "kit"), ct);
            throw new InvalidOperationException("Simulated failure before commit");
        }, default));
        await store.ExecuteAsync(async (s, ct) => { Assert.Null(await s.Blanks.FindAsync("blank", ct)); Assert.Empty(await s.Outbox.GetPendingAsync(10, ct)); return true; }, default);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RepositoryContractPersistsKitAndSnapshotAndCompletedArtifact(bool sqlite)
    {
        await using var fixture = new RepositoryFixture(); var store = await fixture.CreateAsync(sqlite);
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
            pending.Complete("token", "/artifact"); await s.PrintJobs.StoreArtifactAsync(pending.Id, [1, 2, 3], ct); await s.PrintJobs.SaveAsync(pending, ct); return true;
        }, default);
        await store.ExecuteAsync(async (s, ct) =>
        {
            Assert.Null(await s.PrintJobs.FindNextPendingAsync(now.AddHours(1), ct));
            Assert.Equal(PrintStatus.Completed, (await s.PrintJobs.FindAsync("job", ct))?.Status);
            Assert.Equal(new byte[] { 1, 2, 3 }, await s.PrintJobs.FindArtifactAsync("job", ct)); return true;
        }, default);
    }


}
