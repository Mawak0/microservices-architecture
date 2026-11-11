using Npgsql;
using Olympiad.Persistence.Postgres;
using Olympiad.Workflows.Application;
using Olympiad.Workflows.Domain;
using Olympiad.Workflows.Infrastructure;
using Xunit;
using Process = Olympiad.Workflows.Domain.Process;

namespace Olympiad.Workflows.Tests;

public sealed class PostgresProcessTests
{
    private static async Task<(PostgresProcesses Store, string Connection)> Store()
    {
        var connection = TestDatabase.For(Guid.NewGuid().ToString());
        var store = new PostgresProcesses(new PostgresStore(connection));
        await store.InitializeAsync(default);
        return (store, connection);
    }

    [Fact]
    public async Task DuplicateDeliveryHasOneIdentityAndChangedPayloadConflicts()
    {
        var (store, connection) = await Store();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.AcceptAsync(ProcessTests.New(), default)));
        Assert.Single(results.Select(s => s.Id).Distinct());
        await Assert.ThrowsAsync<ProcessKeyConflictException>(() => store.AcceptAsync(ProcessTests.New(fingerprint: "changed"), default));
        Assert.Null(await store.FindAsync(results[0].Id, "someone-else", default));
        Assert.NotNull(await store.FindAsync(results[0].Id, "*", default));
        var claim = await store.ClaimAsync(results[0].Id, "one", ProcessTests.Now, TimeSpan.FromSeconds(10), default);
        Assert.NotNull(claim);
        Assert.Null(await store.ClaimAsync(null, "two", ProcessTests.Now, TimeSpan.FromSeconds(10), default));
        await store.FinishAsync(claim.Id, "wrong", new(ProcessTests.Result), ProcessTests.Now, TimeSpan.Zero, TimeSpan.Zero, default);
        var restarted = new PostgresProcesses(new PostgresStore(connection));
        await restarted.FinishAsync(claim.Id, "one", new(ProcessTests.Result), ProcessTests.Now, TimeSpan.Zero, TimeSpan.Zero, default);
        var done = await restarted.FindAsync(claim.Id, "observer", default);
        Assert.Equal(ProcessStatus.Completed, done!.Status);
        Assert.Null(done.Invocation);
        await restarted.CompensatedAsync(claim.Id, "one", ProcessTests.Now, default);
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var history = new NpgsqlCommand("SELECT COUNT(*) FROM process_history WHERE process_id=@id", db);
        history.Parameters.AddWithValue("id", claim.Id);
        Assert.Equal(2L, await history.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task CheckpointBeforeOrAfterReplySurvivesRestart(bool early, bool rejection)
    {
        var (store, connection) = await Store();
        var accepted = await store.AcceptAsync(ProcessTests.New("PdfReady"), default);
        await store.ClaimAsync(accepted.Id, "worker", ProcessTests.Now, TimeSpan.FromMinutes(1), default);
        async Task Confirm() { if (rejection) await store.RejectCheckpointAsync("PdfReady", "answer", "PDF_RENDER_FAILED", default); else await store.ConfirmAsync("PdfReady", "answer", default); }
        if (early) await Confirm();
        await store.FinishAsync(accepted.Id, "worker", new(ProcessTests.Result), ProcessTests.Now, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), default);
        if (!early)
        {
            Assert.Equal(ProcessStatus.AwaitingCheckpoint, (await store.FindAsync(accepted.Id, "observer", default))!.Status);
            await Confirm();
        }
        var restarted = new PostgresProcesses(new PostgresStore(connection));
        var result = await restarted.FindAsync(accepted.Id, "observer", default);
        Assert.Equal(rejection ? ProcessStatus.Failed : ProcessStatus.Completed, result!.Status);
        Assert.Null(result.Invocation);
        Assert.Equal(rejection ? 422 : 201, result.Result!.StatusCode);
        await Confirm();
        Assert.Null(await restarted.ClaimAsync(accepted.Id, "late", ProcessTests.Now.AddDays(1), TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task ExpiredWorkerCannotOverwriteReplacementAndRetriesRemainDurable()
    {
        var (store, _) = await Store();
        var accepted = await store.AcceptAsync(ProcessTests.New("ReviewRegistered"), default);
        await store.ClaimAsync(accepted.Id, "old", ProcessTests.Now, TimeSpan.FromSeconds(1), default);
        await store.ClaimAsync(accepted.Id, "new", ProcessTests.Now.AddSeconds(1), TimeSpan.FromSeconds(10), default);
        await store.FinishAsync(accepted.Id, "old", new(ProcessTests.Result), ProcessTests.Now.AddSeconds(1), TimeSpan.Zero, TimeSpan.Zero, default);
        await store.FinishAsync(accepted.Id, "new", new(new(503, "application/json", []), true, "OFFLINE"), ProcessTests.Now.AddSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), default);
        Assert.Null(await store.ClaimAsync(null, "early", ProcessTests.Now.AddSeconds(2), TimeSpan.FromSeconds(10), default));
        var retry = await store.ClaimAsync(null, "retry", ProcessTests.Now.AddSeconds(3), TimeSpan.FromSeconds(10), default);
        Assert.Equal(3, retry!.Attempts);
        Assert.NotNull(retry.Invocation);
        await store.FinishAsync(retry.Id, "retry", new(new(409, "application/json", [], "orphan"), false, "CLOSED", true), ProcessTests.Now.AddSeconds(3), TimeSpan.Zero, TimeSpan.Zero, default);
        var compensation = await store.ClaimAsync(null, "cleanup", ProcessTests.Now.AddSeconds(3), TimeSpan.FromSeconds(10), default);
        Assert.Equal(ProcessStatus.Compensating, compensation!.Status);
        await store.CompensatedAsync(compensation.Id, "cleanup", ProcessTests.Now.AddSeconds(3), default);
        Assert.Equal(ProcessStatus.Failed, (await store.FindAsync(compensation.Id, "observer", default))!.Status);
    }
}
