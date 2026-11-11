using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;
using Xunit;

namespace Olympiad.Materials.Tests;

public sealed class ArtifactQueueTests
{
    [Fact]
    public async Task StorageFailureLeavesRecoverableJobWithoutReadyPdfOrEvent()
    {
        var store = new MemoryTransactions(); var clock = new StubDependencies(); var artifacts = new MemoryArtifacts { Unavailable=true };
        await Add(store);
        var processor = new ProcessPrintJobs(store, new Renderer(), clock, artifacts);
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => processor.ExecuteNextAsync(default));
        Assert.Null(await store.ExecuteAsync((tx, ct) => tx.PrintJobs.FindArtifactAsync("job", ct), default));
        Assert.Empty(await store.ExecuteAsync((tx, ct) => tx.Outbox.GetPendingAsync(100, ct), default));
        artifacts.Unavailable=false; clock.Now=clock.Now.AddMinutes(11);
        Assert.True(await processor.ExecuteNextAsync(default));
        var queries = new MaterialQueries(store, new NoTokens(), artifacts);
        Assert.Equal(new byte[] { 5 }, await queries.ArtifactAsync(Sample.Admin().Actor, "job", default));
        Assert.False(await processor.ExecuteNextAsync(default));
    }
    [Fact]
    public async Task ExpiredWorkerCannotOverwritePdfProducedByReplacement()
    {
        var store = new MemoryTransactions(); var clock = new StubDependencies(); var artifacts = new MemoryArtifacts(); var renderer = new BlockingRenderer();
        await Add(store);
        var old = new ProcessPrintJobs(store, renderer, clock, artifacts).ExecuteNextAsync(default);
        await renderer.Started.Task;
        clock.Now=clock.Now.AddMinutes(11);
        Assert.True(await new ProcessPrintJobs(store, new Renderer(), clock, artifacts).ExecuteNextAsync(default));
        renderer.Release.SetResult(); await old;
        var queries = new MaterialQueries(store, new NoTokens(), artifacts);
        Assert.Equal(new byte[] { 5 }, await queries.ArtifactAsync(Sample.Admin().Actor, "job", default));
        Assert.Single(await store.ExecuteAsync((tx, ct) => tx.Outbox.GetPendingAsync(100, ct), default));
    }
    private static Task<bool> Add(MemoryTransactions store) => store.ExecuteAsync(async (tx, ct) => { await tx.PrintJobs.AddAsync(new("job", "game", "team", Sample.Snapshot()), ct); return true; }, default);
    private sealed class Renderer : IPdfRenderer { public Task<byte[]> RenderAsync(PrintSnapshot s,CancellationToken ct) => Task.FromResult(new byte[] { 5 }); }
    private sealed class BlockingRenderer : IPdfRenderer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> RenderAsync(PrintSnapshot s,CancellationToken ct) { Started.SetResult(); await Release.Task; return [9]; }
    }
    private sealed class NoTokens : IBlankTokenProtector
    {
        public string Protect(BlankIdentity value) => throw new NotSupportedException();
        public BlankIdentity? Unprotect(string token) => throw new NotSupportedException();
    }
}
