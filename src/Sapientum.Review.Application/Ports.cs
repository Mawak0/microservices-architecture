using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
public interface IClock { Task<DateTimeOffset> Now(CancellationToken cancellationToken); }
public interface IGameProfiles { Task<GameProfile> Get(string id, CancellationToken cancellationToken); }
public interface IAcceptedReceipts { Task<Receipt> Get(string id, CancellationToken cancellationToken); }
public interface ITaskAnswers { Task<string> Get(string taskId, CancellationToken cancellationToken); }
public sealed record AuthenticatedActor(string Id, IReadOnlyList<string> Roles);
public interface IIdentityProvider { Task<AuthenticatedActor?> Authenticate(string token, CancellationToken cancellationToken); }

public interface ICheckRepository
{
    AnswerCheck? Find(string id);
    AnswerCheck? FindByReceipt(string receiptId);
    IReadOnlyList<AnswerCheck> ForGame(string gameId);
    void Save(AnswerCheck check);
}
public interface IAppealRepository
{
    Appeal? Find(string id);
    bool Exists(string gameId, string teamId, string taskId);
    IReadOnlyList<Appeal> ForGame(string gameId);
    void Save(Appeal appeal);
}
public interface IQuotaRepository { GameAppealQuota Get(string gameId); void Save(GameAppealQuota quota); }
public interface ICompletionRepository
{
    GameReviewCompletion Get(string gameId);
    IReadOnlyList<string> TrackedGames();
    void Save(GameReviewCompletion completion);
}
public interface IIncomingEvents { string? ReceiptForEvent(string eventId); void RememberReceipt(string eventId, string receiptId); }
public interface IEventOutbox { void Add(ReviewEvent message); }
public interface IReviewSession
{
    ICheckRepository Checks { get; }
    IAppealRepository Appeals { get; }
    IQuotaRepository Quotas { get; }
    ICompletionRepository Completions { get; }
    IIncomingEvents Inbox { get; }
    IEventOutbox Events { get; }
}
public sealed record CommandIdentity(string ActorId, string Operation, string Key, string Fingerprint);
public sealed record Outcome<T>(T Value, bool Created = false);
public interface ITransactions
{
    Task<Outcome<T>?> Replay<T>(CommandIdentity identity, CancellationToken cancellationToken);
    Task<Outcome<T>> Run<T>(CommandIdentity? identity,
        Func<IReviewSession, Outcome<T>> action, CancellationToken cancellationToken);
}
public interface IEventPublisher { Task Publish(ReviewEvent message, CancellationToken cancellationToken); }
public interface IOutboxDelivery { Task<int> Drain(CancellationToken cancellationToken); }

public abstract record EventData(string GameId, int AdmissionVersion);
public sealed record VerdictEventData(string GameId, int AdmissionVersion, string CheckId,
    string ReceiptId, string BlankId, string TeamId, string TaskId, string Verdict, int Revision)
    : EventData(GameId, AdmissionVersion);
public sealed record AppealEventData(string GameId, int AdmissionVersion, string AppealId,
    string CheckId, string TeamId, string TaskId, string? Outcome = null)
    : EventData(GameId, AdmissionVersion);
public sealed record ReturnedUsageData(string GameId, int AdmissionVersion, string AppealId, int Amount = 1)
    : EventData(GameId, AdmissionVersion);
public sealed record CompletionEventData(string GameId, int AdmissionVersion, IReadOnlyList<string> ReceiptIds)
    : EventData(GameId, AdmissionVersion);
public sealed record ReviewEvent(string EventId, string Type, DateTimeOffset OccurredAt,
    string AggregateId, EventData Data);
