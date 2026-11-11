using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;
using Olympiad.Intake.Infrastructure.Persistence;
using Xunit;

namespace Olympiad.Intake.Tests;

public sealed class AcceptanceStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "intake-acceptance-" + Guid.NewGuid());
    private readonly Dependencies dependencies = new();
    private PostgresDatabase? database;
    private async Task<AcceptBlank> Prepare()
    {
        database = new PostgresDatabase(TestDatabase.For(Path.Combine(directory, "answers.db")));
        await database.InitializeAsync();
        await database.ExecuteAsync(async (tx, ct) =>
        {
            await tx.Sessions.SaveAsync(IntakeSession.Open(dependencies.Admission, 1, dependencies.Now), ct);
            return true;
        }, default);
        return new(database, dependencies, dependencies, dependencies, dependencies,
            new SubmissionFinalizer(database, dependencies, dependencies, dependencies, dependencies), dependencies, new());
    }
    private static SubmitBlank Command => new("registered", new byte[] { 1, 2, 3 }, "image/png", true);

    [Fact]
    public async Task AcceptedAnswerAndItsDeliveryEventAreDurableTogether()
    {
        var accept = await Prepare(); var first = await accept.ExecuteAsync(Command, new("observer", "key", "body"), default);
        var stored = await database!.ExecuteAsync((tx, ct) => tx.Receipts.FindAsync(first.Receipt.Id, ct), default);
        Assert.NotNull(stored);
        Assert.Equal(first.Receipt.Id, stored.Id);
        Assert.Equal(first.Receipt.Blank, stored.Blank);
        Assert.Equal(first.Receipt.AdmissionVersion, stored.AdmissionVersion);
        Assert.Equal(first.Receipt.ImageRef, stored.ImageRef);
        Assert.Equal(first.Receipt.AcceptedAt, stored.AcceptedAt);
        var message = await database.ExecuteAsync((tx, ct) => tx.Outbox.ClaimAsync("sender", dependencies.Now, dependencies.Now.AddSeconds(30), ct), default);
        Assert.Equal(first.Receipt.Id, Assert.IsType<ReceiptAccepted>(message!.Event).Data.ReceiptId);
    }
    [Fact]
    public async Task SameSubmissionReplaysButNewAttemptToUseBlankIsRejected()
    {
        var accept = await Prepare(); var identity = new OperationIdentity("observer", "key", "body");
        var first = await accept.ExecuteAsync(Command, identity, default);
        var replay = await accept.ExecuteAsync(Command, identity, default);
        Assert.True(replay.Replayed); Assert.Equal(first.Receipt.Id, replay.Receipt.Id);
        var rejection = await Assert.ThrowsAsync<IntakeException>(() => accept.ExecuteAsync(Command, identity with { Key = "different" }, default));
        Assert.Equal(IntakeError.BlankAlreadyAccepted, rejection.Error);
    }
    [Fact]
    public async Task ImageStorageFailureDoesNotConsumeBlank()
    {
        var accept = await Prepare(); dependencies.StorageUnavailable = true;
        var identity = new OperationIdentity("observer", "key", "body");
        Assert.Equal(IntakeError.StorageUnavailable, (await Assert.ThrowsAsync<IntakeException>(() => accept.ExecuteAsync(Command, identity, default))).Error);
        Assert.False(await database!.ExecuteAsync((tx, ct) => tx.Receipts.ExistsForBlankAsync("blank", ct), default));
        dependencies.StorageUnavailable = false;
        Assert.False((await accept.ExecuteAsync(Command, identity, default)).Replayed);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class Dependencies : IAdmissionGateway, ITeamAdmissionGateway, IBlankRegistry, IImageStorage, IImageQrDecoder, IClock
    {
        public DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-11-08T00:00:00Z");
        public Admission Admission => new("game", 1, "running", Now.AddHours(1), false);
        public bool StorageUnavailable { get; set; }
        Task<Admission> IAdmissionGateway.GetAsync(string id, CancellationToken ct) => Task.FromResult(Admission);
        Task<TeamAdmission> ITeamAdmissionGateway.GetAsync(string id, CancellationToken ct) => Task.FromResult(new TeamAdmission("team", "game", true));
        public Task<BlankBinding> VerifyAsync(string token, CancellationToken ct) => Task.FromResult(new BlankBinding("blank", "game", "team", "task", "1"));
        public Task<string> SaveAsync(string receiptId, string blankId, ReadOnlyMemory<byte> image, string mediaType, CancellationToken ct)
            => StorageUnavailable ? throw new IntakeException(IntakeError.StorageUnavailable, "Storage down") : Task.FromResult("protected://answer");
        public string Decode(ReadOnlyMemory<byte> image, string mediaType) => "registered";
        public ValueTask<DateTimeOffset> NowAsync(CancellationToken ct) => ValueTask.FromResult(Now);
    }
}
