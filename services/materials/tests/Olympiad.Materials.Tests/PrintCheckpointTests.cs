using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;
using Olympiad.Materials.Infrastructure.Persistence;
using Olympiad.Persistence.Postgres;
using Olympiad.Workflows.Domain;
using Olympiad.Workflows.Infrastructure;
using Xunit;
using Process = Olympiad.Workflows.Domain.Process;

namespace Olympiad.Materials.Tests;

public sealed class PrintCheckpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryConfirmsOnlyDurablePdfOrRecordsDefinitiveRenderFailure(bool failed)
    {
        var connection = TestDatabase.For(Guid.NewGuid().ToString()); var database = new PostgresDatabase(connection);
        await database.InitializeAsync();
        var processes = new PostgresProcesses(new PostgresStore(connection)); await processes.InitializeAsync(default);
        var now = DateTimeOffset.UtcNow;
        var process = Process.Accept("operation", "admin", "POST:/print-jobs", "key", "hash",
            new("/print-jobs", "POST", "/print-jobs", "{}", "{}", "{}", [1]), "PdfReady", now);
        await processes.AcceptAsync(process, default);
        await processes.ClaimAsync("operation", "worker", now, TimeSpan.FromMinutes(1), default);
        await processes.FinishAsync("operation", "worker", new(new(202, "application/json", [], "job")), now, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), default);
        var transactions = new PostgresMaterialsTransactions(database);
        await transactions.ExecuteAsync(async (s, ct) => { await s.PrintJobs.AddAsync(new("job", "game", "team", Sample.Snapshot()), ct); return true; }, default);
        var probe = new PrintCheckpointProbe(database, processes);
        await probe.PollAsync(default);
        Assert.Equal(ProcessStatus.AwaitingCheckpoint, (await processes.FindAsync("operation", "admin", default))!.Status);
        await transactions.ExecuteAsync(async (s, ct) =>
        {
            var job = (await s.PrintJobs.FindAsync("job", ct))!; job.TryStart("renderer", now, TimeSpan.FromMinutes(1));
            if (failed) job.Fail("renderer", new("PDF_RENDER_FAILED", "Invalid template"));
            else job.Complete("renderer", "/print-jobs/job/artifact");
            await s.PrintJobs.SaveAsync(job, ct); return true;
        }, default);
        await probe.PollAsync(default);
        if (!failed)
        {
            Assert.Equal(ProcessStatus.AwaitingCheckpoint, (await processes.FindAsync("operation", "admin", default))!.Status);
            await transactions.ExecuteAsync(async (s, ct) => { await s.PrintJobs.StoreArtifactAsync("job", new("key", "application/pdf", 4, "digest"), ct); return true; }, default);
        }
        // New adapter instances reconcile a result committed before the worker stopped.
        await new PrintCheckpointProbe(new PostgresDatabase(connection), new PostgresProcesses(new PostgresStore(connection))).PollAsync(default);
        var result = await processes.FindAsync("operation", "admin", default);
        Assert.Equal(failed ? ProcessStatus.Failed : ProcessStatus.Completed, result!.Status);
        Assert.Null(result.Invocation);
        await probe.PollAsync(default);
    }
}
