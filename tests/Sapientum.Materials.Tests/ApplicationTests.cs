using Sapientum.Materials.Application;
using Sapientum.Materials.Domain;
using Sapientum.Materials.Infrastructure.Security;
using Xunit;

namespace Sapientum.Materials.Tests;

public sealed class ApplicationTests
{
    private static HmacBlankTokenProtector Tokens() => new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    [Fact] public async Task SuccessfulIssuanceReplaySurvivesDependencyOutageAndRevokedAdmission()
    {
        var dependency = new StubDependencies(); var transactions = new MemoryTransactions();
        var handler = new IssueBlanks(dependency, dependency, transactions, Tokens(), dependency);
        var command = new IssueBlanksCommand("game", "team", "task-1", 2, null);
        var first = await handler.ExecuteAsync(Sample.Admin(), command, default); var calls = dependency.Calls;
        dependency.Unavailable = true; dependency.Admitted = false;
        var repeat = await handler.ExecuteAsync(Sample.Admin(), command, default);
        Assert.Equal(first.Select(b => b.Id), repeat.Select(b => b.Id)); Assert.Equal(calls, dependency.Calls);
        Assert.Single(await transactions.ExecuteAsync((s, ct) => s.Outbox.GetPendingAsync(100, ct), default));
    }
    [Fact] public async Task AuthorizationIsCheckedBeforeReplayAndBeforeDependencies()
    {
        var dependency = new StubDependencies(); var handler = new IssueBlanks(dependency, dependency, new MemoryTransactions(), Tokens(), dependency);
        await Assert.ThrowsAsync<AccessDeniedException>(() => handler.ExecuteAsync(new(new("admin", ["observer"]), "key"), new("game", "team", "task-1", 1, null), default));
        Assert.Equal(0, dependency.Calls);
    }
    [Fact] public async Task ReusingKeyWithDifferentPayloadIsDetectedWithoutExternalCalls()
    {
        var dependency = new StubDependencies(); var handler = new IssueBlanks(dependency, dependency, new MemoryTransactions(), Tokens(), dependency);
        await handler.ExecuteAsync(Sample.Admin(), new("game", "team", "task-1", 1, null), default); dependency.Unavailable = true;
        var exception = await Assert.ThrowsAsync<DomainException>(() => handler.ExecuteAsync(Sample.Admin(), new("game", "team", "task-1", 2, null), default));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", exception.Code);
    }
    [Fact] public async Task KitReplayUsesOriginalSettingsWhenDependencyIsUnavailable()
    {
        var dependency = new StubDependencies(); var transactions = new MemoryTransactions(); var handler = new PrepareKit(dependency, dependency, transactions, Tokens(), dependency);
        var original = await handler.ExecuteAsync(Sample.Admin(), new("game", "team"), default); dependency.Unavailable = true;
        Assert.Same(original, await handler.ExecuteAsync(Sample.Admin(), new("game", "team"), default));
        Assert.Equal(3, original.BlankIds.Count);
    }
    [Fact] public async Task PrintingCapturesSnapshotAndReplayDoesNotReadChangedTemplate()
    {
        var dependency = new StubDependencies(); var transactions = new MemoryTransactions();
        var handler = new CreatePrintJob(dependency, dependency, dependency, dependency, transactions);
        var command = new CreatePrintJobCommand("game", "team", [], ["task-1"]);
        var first = await handler.ExecuteAsync(Sample.Admin(), command, default);
        dependency.Game = Sample.Game(key: "changed"); dependency.Unavailable = true;
        var repeat = await handler.ExecuteAsync(Sample.Admin(), command, default);
        Assert.Equal(first.Id, repeat.Id); Assert.Equal("layout/v1", repeat.Snapshot.Template.Key);
    }
    [Fact] public async Task OutboxRetainsStableIdentityAcrossTransientFailure()
    {
        var store = new MemoryTransactions(); var publisher = new RecordingPublisher { Unavailable = true };
        var message = new MaterialEvent("event", "KitPrepared", DateTimeOffset.UtcNow, "kit", "game", "team", KitId: "kit");
        await store.ExecuteAsync(async (s, ct) => { await s.Outbox.AddAsync(message, ct); return true; }, default);
        var processor = new PublishOutbox(store, publisher);
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => processor.ExecuteAsync(default));
        publisher.Unavailable = false; Assert.Equal(1, await processor.ExecuteAsync(default)); Assert.Equal(0, await processor.ExecuteAsync(default));
        Assert.Equal(publisher.Attempts[0], publisher.Attempts[1]);
    }
    [Fact] public async Task RealRegistryAndSignatureAreBothRequired()
    {
        var tokens = Tokens(); var transactions = new MemoryTransactions(); var dependency = new StubDependencies();
        var issued = await new IssueBlanks(dependency, dependency, transactions, tokens, dependency).ExecuteAsync(Sample.Admin(), new("game", "team", "task-1", 1, null), default);
        var queries = new MaterialQueries(transactions, tokens);
        Assert.Equal(issued[0].Id, (await queries.VerifyAsync(Sample.Admin().Actor, issued[0].QrToken, default)).Id);
        var unregistered = tokens.Protect(new("missing", 99, "game", "team", "task-1", "1", null));
        Assert.Equal("INVALID_QR", (await Assert.ThrowsAsync<DomainException>(() => queries.VerifyAsync(Sample.Admin().Actor, unregistered, default))).Code);
    }
}
