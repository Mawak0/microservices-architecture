using Olympiad.Review.Application;
using Olympiad.Review.Domain;
using Xunit;
namespace Olympiad.Review.Tests;
public sealed class SubstitutionTests
{
    [Fact]
    public async Task VerdictUseCaseRunsThroughSubstitutedPortsWithoutSqlOrHttp()
    {
        var store = new MemoryTransactions();
        var check = DomainTests.Check(); check.Claim("checker", "token", DomainTests.Now, TimeSpan.FromMinutes(1), false);
        store.Checks.Save(check);
        var command = new RecordVerdict(store, new FixedClock());
        var identity = new CommandIdentity("checker", "verdict", "key", "fingerprint");
        var result = await command.Execute(identity, check.Id, "token", Verdict.Correct, default);
        var repeated = await command.Execute(identity, check.Id, "token", Verdict.Correct, default);
        Assert.Equal(1, result.Value.Revision); Assert.Equal(1, repeated.Value.Revision);
        Assert.Equal("correct", result.Value.Verdict);
        Assert.Equal("VerdictIssued", Assert.Single(store.Messages).Type);
        Assert.Null(store.Checks.Find(check.Id)!.Assignment);
    }
    private sealed class FixedClock : IClock
    {
        public Task<DateTimeOffset> Now(CancellationToken cancellationToken) => Task.FromResult(DomainTests.Now);
    }
    private sealed class MemoryTransactions : ITransactions, IReviewSession, IEventOutbox
    {
        private readonly Dictionary<string, (string Fingerprint, object Value)> responses = [];
        public MemoryChecks Checks { get; } = new();
        ICheckRepository IReviewSession.Checks => Checks;
        public List<ReviewEvent> Messages { get; } = [];
        public IAppealRepository Appeals => throw new InvalidOperationException("Verdict must not access appeals.");
        public IQuotaRepository Quotas => throw new InvalidOperationException("Verdict must not access quota.");
        public ICompletionRepository Completions => throw new InvalidOperationException("Verdict must not access completion.");
        public IIncomingEvents Inbox => throw new InvalidOperationException("Verdict must not access inbox.");
        public IEventOutbox Events => this;
        public void Add(ReviewEvent message) => Messages.Add(message);
        public Task<Outcome<T>?> Replay<T>(CommandIdentity identity, CancellationToken cancellationToken)
        {
            var key = identity.ActorId + "/" + identity.Operation + "/" + identity.Key;
            if (!responses.TryGetValue(key, out var prior)) return Task.FromResult<Outcome<T>?>(null);
            if (prior.Fingerprint != identity.Fingerprint) throw new DomainFailure("IDEMPOTENCY_KEY_REUSED");
            return Task.FromResult<Outcome<T>?>((Outcome<T>)prior.Value);
        }
        public async Task<Outcome<T>> Run<T>(CommandIdentity? identity, Func<IReviewSession, Outcome<T>> action,
            CancellationToken cancellationToken)
        {
            if (identity is not null && await Replay<T>(identity, cancellationToken) is { } replay) return replay;
            var result = action(this);
            if (identity is not null)
                responses[identity.ActorId + "/" + identity.Operation + "/" + identity.Key] = (identity.Fingerprint, result);
            return result;
        }
    }
    private sealed class MemoryChecks : ICheckRepository
    {
        private readonly Dictionary<string, CheckSnapshot> values = [];
        public AnswerCheck? Find(string id) => values.TryGetValue(id, out var value) ? AnswerCheck.Restore(value) : null;
        public AnswerCheck? FindByReceipt(string receiptId) => values.Values
            .Where(v => v.Receipt.Id == receiptId).Select(AnswerCheck.Restore).FirstOrDefault();
        public IReadOnlyList<AnswerCheck> ForGame(string gameId) => values.Values
            .Where(v => v.Receipt.GameId == gameId).Select(AnswerCheck.Restore).ToArray();
        public void Save(AnswerCheck check) => values[check.Id] = check.Snapshot();
    }
}
