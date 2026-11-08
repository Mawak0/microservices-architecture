using Microsoft.Data.Sqlite;
using Sapientum.Review.Application;
using Sapientum.Review.Domain;
using Sapientum.Review.Infrastructure;
using Xunit;
namespace Sapientum.Review.Tests;
public sealed class PersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sapientum-review-tests-" + Guid.NewGuid());
    private SqliteDatabase Database()
    {
        var database = new SqliteDatabase("Data Source=" + Path.Combine(directory, "review.db"));
        database.Initialize(); return database;
    }
    private static CommandIdentity Identity(string key = "key", string actor = "actor") => new(actor, "verdict", key, "hash");
    [Fact]
    public async Task RestartRetainsCheckLeaseFencingAndSuccessfulResponse()
    {
        var tx = new SqliteTransactions(Database()); var c = DomainTests.Check();
        c.Claim("actor", "token", DomainTests.Now, TimeSpan.FromMinutes(1), false);
        await tx.Run<CheckView>(null, s => { s.Checks.Save(c); return new(CheckView.From(c)); }, default);
        var first = await new RecordVerdict(tx, new FixedClock()).Execute(Identity(), c.Id, "token", Verdict.Incorrect, default);
        var restarted = new SqliteTransactions(Database());
        var replay = await new RecordVerdict(restarted, new OfflineClock()).Execute(Identity(), c.Id, "token", Verdict.Incorrect, default);
        Assert.Equal(first.Value.Revision, replay.Value.Revision); Assert.Equal(first.Value.History, replay.Value.History);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", (await Assert.ThrowsAsync<DomainFailure>(() =>
            restarted.Replay<CheckView>(Identity() with { Fingerprint = "changed-token" }, default))).Code);
        Assert.Null(await restarted.Replay<CheckView>(Identity(actor: "other"), default));
    }
    [Fact]
    public async Task TransactionRollsBackAggregateAndOutboxTogether()
    {
        var database = Database(); var tx = new SqliteTransactions(database); var c = DomainTests.Check();
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.Run<int>(null, s =>
        {
            s.Checks.Save(c); s.Events.Add(Event()); throw new InvalidOperationException("failure before commit");
        }, default));
        var count = (await tx.Run<int>(null, s => new(s.Checks.ForGame("game").Count), default)).Value;
        Assert.Equal(0, count); Assert.Equal(0, await new OutboxDispatcher(database, new RecordingPublisher()).Drain(default));
    }
    [Fact]
    public async Task SeparateConnectionsCannotOverconsumeQuota()
    {
        var tx = new SqliteTransactions(Database());
        await tx.Run<int>(null, s => { s.Quotas.Save(new("game", 1)); return new(0); }, default);
        async Task<string> Consume()
        {
            try
            {
                await Task.Run(() => new SqliteTransactions(Database()).Run<int>(null, s =>
                {
                    var quota = s.Quotas.Get("game"); quota.Use(); s.Quotas.Save(quota); return new(quota.Used);
                }, default)); return "success";
            }
            catch (DomainFailure failure) { return failure.Code; }
        }
        var results = await Task.WhenAll(Consume(), Consume());
        Assert.Single(results, r => r == "success"); Assert.Single(results, r => r == "APPEAL_QUOTA_EXHAUSTED");
        Assert.Equal(1, (await tx.Run<int>(null, s => new(s.Quotas.Get("game").Used), default)).Value);
    }
    [Fact]
    public async Task SqlUniqueConstraintsProtectReceiptAndAppealIdentity()
    {
        var tx = new SqliteTransactions(Database()); var first = DomainTests.Checked();
        await tx.Run<int>(null, s => { s.Checks.Save(first); s.Appeals.Save(Appeal.Open("first", first, "request")); return new(0); }, default);
        var duplicate = AnswerCheck.Create("different", first.Receipt, "4");
        await Assert.ThrowsAsync<SqliteException>(() => tx.Run<int>(null, s => { s.Checks.Save(duplicate); return new(0); }, default));
        await Assert.ThrowsAsync<SqliteException>(() => tx.Run<int>(null, s =>
        {
            s.Appeals.Save(Appeal.Open("second", first, "request")); return new(0);
        }, default));
    }

    [Fact]
    public async Task DuplicateDeliveryUsesPersistedCaseWithoutExternalDependencies()
    {
        var tx = new SqliteTransactions(Database()); var c = DomainTests.Check();
        await tx.Run<int>(null, s => { s.Checks.Save(c); return new(0); }, default);
        var result = await new ReceiveReceipt(tx, new OfflineReceipts(), new OfflineTasks())
            .Execute(Identity(), "new-event", c.Receipt, default);
        Assert.Equal(c.Id, result.Value.Id); Assert.Equal("4", result.Value.ReferenceAnswer);
        var changed = c.Receipt with { TaskId = "other" };
        var failure = await Assert.ThrowsAsync<DomainFailure>(() => new ReceiveReceipt(tx, new OfflineReceipts(), new OfflineTasks())
            .Execute(Identity("other-key"), "other-event", changed, default));
        Assert.Equal("RECEIPT_MISMATCH", failure.Code);
    }
    private static ReviewEvent Event() => new("stable-event", "AllChecksCompleted", DomainTests.Now, "game",
        new CompletionEventData("game", 1, []));
    private sealed class RecordingPublisher : IEventPublisher
    {
        public bool Available { get; init; } = true;
        public List<ReviewEvent> Messages { get; } = [];
        public Task Publish(ReviewEvent message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Available ? Task.CompletedTask : Task.FromException(new DomainFailure("OFFLINE", FailureKind.Dependency));
        }
    }
    private sealed class FixedClock : IClock { public Task<DateTimeOffset> Now(CancellationToken ct) => Task.FromResult(DomainTests.Now); }
    private sealed class OfflineClock : IClock { public Task<DateTimeOffset> Now(CancellationToken ct) => throw new InvalidOperationException("Clock called during replay"); }
    private sealed class OfflineReceipts : IAcceptedReceipts { public Task<Receipt> Get(string id, CancellationToken ct) => throw new InvalidOperationException("Receipt dependency called"); }
    private sealed class OfflineTasks : ITaskAnswers { public Task<string> Get(string id, CancellationToken ct) => throw new InvalidOperationException("Task dependency called"); }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
