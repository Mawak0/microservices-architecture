using Sapientum.Intake.Application;
using Sapientum.Intake.Domain;
using Sapientum.Intake.Infrastructure.Persistence;
using Xunit;

namespace Sapientum.Intake.Tests;

public sealed class PersistenceAndApplicationTests
{
    [Fact]
    public async Task RollbackLeavesNoAggregateOrOutboxEffect()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Database.ExecuteAsync<bool>(async (tx, ct) =>
        {
            await tx.Sessions.SaveAsync(IntakeSession.Open(fixture.Game.Current, 1, fixture.Clock.Now), ct);
            await tx.Outbox.AddAsync(new IntakeClosed("event", fixture.Clock.Now, "game", new("game", 1, [])), ct);
            throw new InvalidOperationException("transaction failure");
        }, default));
        Assert.Null(await fixture.Database.ExecuteAsync((tx, ct) => tx.Sessions.FindAsync("game", ct), default));
        Assert.Null(await fixture.Database.ExecuteAsync((tx, ct) => tx.Outbox.ClaimAsync("worker", fixture.Clock.Now, fixture.Clock.Now.AddSeconds(30), ct), default));
    }
    [Fact]
    public async Task SessionIdempotencyReplaysDuringDependencyOutageAndDetectsChangedBody()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync();
        var operation = new OperationIdentity("admin:open", "key", "body-1");
        var first = await fixture.Open.ExecuteAsync("game", 1, operation, default);
        fixture.Game.Unavailable = true;
        var second = await fixture.Open.ExecuteAsync("game", 1, operation, default);
        SameSession(first, second);
        var exception = await Assert.ThrowsAsync<IntakeException>(() => fixture.Open.ExecuteAsync("game", 2, operation with { Fingerprint = "body-2" }, default));
        Assert.Equal(IntakeError.IdempotencyKeyReused, exception.Error);
    }
    [Fact]
    public async Task CloseIdempotencyReplaysAfterLaterReopeningAndDependencyOutage()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); await fixture.OpenGameAsync();
        fixture.Game.Current = fixture.Game.Current with { Version = 2, SubmissionDeadline = fixture.Clock.Now.AddMinutes(-1) };
        var close = new CloseIntake(fixture.Database, fixture.Game, fixture.Clock);
        var operation = new OperationIdentity("admin:close", "key", "body");
        var first = await close.ExecuteAsync("game", 2, operation, default);
        fixture.Game.Current = fixture.Game.Current with { Version = 3, LateSubmissionAllowed = true };
        fixture.Game.Unavailable = true;
        SameSession(first, await close.ExecuteAsync("game", 2, operation, default));
    }
    [Fact]
    public async Task ReceiptsIdempotencyAndPendingEventsSurviveFreshDatabaseInstance()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); await fixture.OpenGameAsync();
        var command = fixture.Command; var operation = new OperationIdentity("observer:accept", "key", "body");
        var first = await fixture.Accept.ExecuteAsync(command, operation, default);
        var reopened = new SqliteDatabase(fixture.ConnectionString); await reopened.InitializeAsync();
        var stored = await new GetReceipt(reopened).ExecuteAsync(first.Receipt.Id, default);
        Assert.Equal(first.Receipt.ImageRef, stored.ImageRef);
        var saved = await reopened.ExecuteAsync((tx, ct) => tx.Operations.FindAsync(operation, ct), default);
        Assert.Equal(first.Receipt.Id, saved!.Match(operation).ReceiptId);
        var delivery = await reopened.ExecuteAsync((tx, ct) => tx.Outbox.ClaimAsync("worker", fixture.Clock.Now, fixture.Clock.Now.AddMinutes(1), ct), default);
        Assert.IsType<ReceiptAccepted>(delivery!.Event);
        Assert.Equal(first.Receipt.Id, ((ReceiptAccepted)delivery.Event).Data.ReceiptId);
        var duplicate = await Assert.ThrowsAsync<IntakeException>(() => fixture.Accept.ExecuteAsync(command, operation with { Key = "other" }, default));
        Assert.Equal(IntakeError.BlankAlreadyAccepted, duplicate.Error);
    }
    [Fact]
    public async Task OutboxRetriesWithStableEventIdentityAndIgnoresStaleWorkerAcknowledgement()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); await fixture.OpenGameAsync();
        await fixture.Accept.ExecuteAsync(fixture.Command, new("accept", "key", "body"), default);
        var first = await fixture.Database.ExecuteAsync((tx, ct) => tx.Outbox.ClaimAsync("old", fixture.Clock.Now, fixture.Clock.Now.AddSeconds(1), ct), default);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(2);
        var second = await fixture.Database.ExecuteAsync((tx, ct) => tx.Outbox.ClaimAsync("new", fixture.Clock.Now, fixture.Clock.Now.AddSeconds(1), ct), default);
        Assert.Equal(first!.Event.EventId, second!.Event.EventId);
        Assert.Equal(EventSerialization.Serialize(first.Event), EventSerialization.Serialize(second.Event));
        await fixture.Database.ExecuteAsync(async (tx, ct) => { await tx.Outbox.CompleteAsync(first.Event.EventId, "old", ct); return true; }, default);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(2);
        Assert.NotNull(await fixture.Database.ExecuteAsync((tx, ct) => tx.Outbox.ClaimAsync("third", fixture.Clock.Now, fixture.Clock.Now.AddSeconds(1), ct), default));
    }
    [Fact]
    public async Task ImageFailureDoesNotConsumeBlankAndRetryCanSucceed()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); await fixture.OpenGameAsync();
        fixture.Images.Unavailable = true;
        var operation = new OperationIdentity("accept", "key", "body");
        Assert.Equal(IntakeError.StorageUnavailable, (await Assert.ThrowsAsync<IntakeException>(() => fixture.Accept.ExecuteAsync(fixture.Command, operation, default))).Error);
        fixture.Images.Unavailable = false;
        Assert.False((await fixture.Accept.ExecuteAsync(fixture.Command, operation, default)).Replayed);
    }
    [Fact]
    public async Task ClosingDuringUploadExcludesUnconfirmedSubmissionAndPreventsLateFinalization()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); await fixture.OpenGameAsync();
        fixture.Images.OnSave = async () =>
        {
            fixture.Game.Current = fixture.Game.Current with { Version = 2, SubmissionDeadline = fixture.Clock.Now.AddMinutes(-1) };
            var manifest = await new CloseIntake(fixture.Database, fixture.Game, fixture.Clock).ExecuteAsync("game", 2, new("close", "key", "body"), default);
            Assert.Empty(manifest.ReceiptIds);
        };
        Assert.Equal(IntakeError.IntakeClosed, (await Assert.ThrowsAsync<IntakeException>(() => fixture.Accept.ExecuteAsync(fixture.Command, new("accept", "key", "body"), default))).Error);
        Assert.Empty((await new GetIntakeSession(fixture.Database).ExecuteAsync("game", default)).ReceiptIds);
    }
    [Fact]
    public async Task RestartRecoveryUsesPersistedVerifiedBytesAndStableReceiptIdentity()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); await fixture.OpenGameAsync();
        var pending = new PendingSubmission("interrupted-receipt", fixture.Registry.Blank, new byte[] { 1, 2, 3 }, "image/png",
            new("accept", "recovery", "body"), "dead-worker", fixture.Clock.Now.AddSeconds(-1));
        await fixture.Database.ExecuteAsync(async (tx, ct) => { await tx.Submissions.AddAsync(pending, ct); return true; }, default);
        var reopened = new SqliteDatabase(fixture.ConnectionString);
        var finalizer = new SubmissionFinalizer(reopened, fixture.Images, fixture.Game, fixture.Teams, fixture.Clock);
        Assert.True(await new RecoverPendingSubmission(reopened, finalizer, fixture.Clock).ExecuteAsync(default));
        var receipt = await new GetReceipt(reopened).ExecuteAsync("interrupted-receipt", default);
        Assert.Equal("image://interrupted-receipt", receipt.ImageRef);
        Assert.Equal(new byte[] { 1, 2, 3 }, fixture.Images.LastBytes);
        Assert.Null(await reopened.ExecuteAsync((tx, ct) => tx.Submissions.FindForOperationAsync(pending.Operation, ct), default));
    }
    [Fact]
    public async Task CancellationNeverAcknowledgesAcceptanceAndVerifiedPendingUploadCanRecover()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); await fixture.OpenGameAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Images.OnSave = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Accept.ExecuteAsync(fixture.Command, new("accept", "key", "body"), cancellation.Token));
        Assert.Empty((await new GetIntakeSession(fixture.Database).ExecuteAsync("game", default)).ReceiptIds);
        fixture.Images.OnSave = null; fixture.Clock.Now = fixture.Clock.Now.AddSeconds(31);
        Assert.True(await new RecoverPendingSubmission(fixture.Database, fixture.Finalizer, fixture.Clock).ExecuteAsync(default));
        Assert.Single((await new GetIntakeSession(fixture.Database).ExecuteAsync("game", default)).ReceiptIds);
    }
    [Fact]
    public async Task ConcurrentDifferentKeysForSameBlankHaveExactlyOneAcceptedReceipt()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); await fixture.OpenGameAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try { await fixture.Accept.ExecuteAsync(fixture.Command, new("accept", i.ToString(), "body"), default); return (IntakeError?)null; }
            catch (IntakeException exception) { return exception.Error; }
        }));
        Assert.Single(results, result => result is null);
        Assert.All(results.Where(result => result is not null), result => Assert.Equal(IntakeError.BlankAlreadyAccepted, result));
        Assert.Single((await new GetIntakeSession(fixture.Database).ExecuteAsync("game", default)).ReceiptIds);
    }
    [Fact]
    public async Task ConcurrentSameKeyWaitsForOriginalUploadAndReplaysItsReceipt()
    {
        using var fixture = new Fixture(); await fixture.InitializeAsync(); await fixture.OpenGameAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Images.OnSave = async () => { entered.TrySetResult(); await release.Task; };
        var operation = new OperationIdentity("accept", "same-key", "body");
        var first = fixture.Accept.ExecuteAsync(fixture.Command, operation, default); await entered.Task;
        var second = fixture.Accept.ExecuteAsync(fixture.Command, operation, default);
        release.SetResult(); var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].Receipt.Id, results[1].Receipt.Id); Assert.False(results[0].Replayed); Assert.True(results[1].Replayed);
    }

    private static void SameSession(SessionSnapshot expected, SessionSnapshot actual)
    {
        Assert.Equal(expected.GameId, actual.GameId); Assert.Equal(expected.AdmissionVersion, actual.AdmissionVersion);
        Assert.Equal(expected.Status, actual.Status); Assert.Equal(expected.ReceiptIds, actual.ReceiptIds);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sapientum-unit-" + Guid.NewGuid());
        public string ConnectionString { get; }
        public SqliteDatabase Database { get; }
        public FakeClock Clock { get; } = new();
        public FakeGames Game { get; }
        public FakeTeams Teams { get; } = new();
        public FakeRegistry Registry { get; } = new();
        public FakeImages Images { get; } = new();
        public SynchronizeIntake Open { get; }
        public SubmissionFinalizer Finalizer { get; }
        public AcceptBlank Accept { get; }
        public SubmitBlank Command { get; } = new("token", new byte[] { 1, 2, 3 }, "image/png", true);
        public Fixture()
        {
            Directory.CreateDirectory(directory);
            ConnectionString = $"Data Source={Path.Combine(directory, "intake.db")};Pooling=False;Default Timeout=30";
            Database = new(ConnectionString); Game = new(new("game", 1, "running", Clock.Now.AddHours(1), false));
            Open = new(Database, Game, Clock); Finalizer = new(Database, Images, Game, Teams, Clock);
            Accept = new(Database, Registry, Game, Teams, new FakeDecoder(), Finalizer, Clock, new());
        }
        public Task InitializeAsync() => Database.InitializeAsync();
        public Task<SessionSnapshot> OpenGameAsync() => Open.ExecuteAsync("game", 1, new("open", "key", "body"), default);
        public void Dispose() => Directory.Delete(directory, true);
    }
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-14T00:00:00Z");
        public ValueTask<DateTimeOffset> NowAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Now); }
    }
    private sealed class FakeGames(Admission game) : IAdmissionGateway
    {
        public Admission Current { get; set; } = game;
        public bool Unavailable { get; set; }
        public Task<Admission> GetAsync(string gameId, CancellationToken ct) => Unavailable
            ? throw new IntakeException(IntakeError.DependencyUnavailable, "offline") : Task.FromResult(Current);
    }
    private sealed class FakeTeams : ITeamAdmissionGateway
    { public Task<TeamAdmission> GetAsync(string teamId, CancellationToken ct) => Task.FromResult(new TeamAdmission("team", "game", true)); }
    private sealed class FakeRegistry : IBlankRegistry
    {
        public BlankBinding Blank { get; } = new("blank", "game", "team", "task", "1");
        public Task<BlankBinding> VerifyAsync(string token, CancellationToken ct) => Task.FromResult(Blank);
    }
    private sealed class FakeDecoder : IImageQrDecoder { public string Decode(ReadOnlyMemory<byte> image, string mediaType) => "token"; }
    private sealed class FakeImages : IImageStorage
    {
        public bool Unavailable { get; set; }
        public Func<Task>? OnSave { get; set; }
        public byte[]? LastBytes { get; private set; }
        public async Task<string> SaveAsync(string receiptId, string blankId, ReadOnlyMemory<byte> image, string mediaType, CancellationToken ct)
        {
            if (Unavailable) throw new IntakeException(IntakeError.StorageUnavailable, "offline");
            LastBytes = image.ToArray(); if (OnSave is not null) await OnSave(); ct.ThrowIfCancellationRequested();
            return $"image://{receiptId}";
        }
    }
}
