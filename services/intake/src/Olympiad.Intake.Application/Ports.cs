using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Application;

public interface IClock { ValueTask<DateTimeOffset> NowAsync(CancellationToken cancellationToken); }
public interface IAdmissionGateway { Task<Admission> GetAsync(string gameId, CancellationToken cancellationToken); }
public interface ITeamAdmissionGateway { Task<TeamAdmission> GetAsync(string teamId, CancellationToken cancellationToken); }
public interface IBlankRegistry { Task<BlankBinding> VerifyAsync(string qrToken, CancellationToken cancellationToken); }
public interface IImageQrDecoder { string Decode(ReadOnlyMemory<byte> image, string mediaType); }
public interface IImageStorage
{
    Task<string> SaveAsync(string receiptId, string blankId, ReadOnlyMemory<byte> image,
        string mediaType, CancellationToken cancellationToken);
}
public sealed record ImageReference(string Key, string ContentType, long Size, string Sha256, string? DownloadRef = null);
public interface IImageReader { Task<byte[]> ReadAsync(ImageReference image, CancellationToken cancellationToken); }
public interface IEventPublisher { Task PublishAsync(IntakeEvent message, CancellationToken cancellationToken); }
public sealed record Actor(string UserId, IReadOnlySet<string> Roles);
public interface IIdentityProvider { Task<Actor?> IntrospectAsync(string token, CancellationToken cancellationToken); }

public sealed record OperationIdentity(string Scope, string Key, string Fingerprint);
public sealed record SessionSnapshot(string GameId, int AdmissionVersion, string Status, IReadOnlyList<string> ReceiptIds);
public sealed record OperationResult(string? ReceiptId = null, SessionSnapshot? Session = null);
public sealed record SavedOperation(string Fingerprint, OperationResult Result)
{
    public OperationResult Match(OperationIdentity identity)
    {
        if (Fingerprint != identity.Fingerprint)
            throw new IntakeException(IntakeError.IdempotencyKeyReused, "The idempotency key belongs to a different request.");
        return Result;
    }
}

public sealed record PendingSubmission(string ReceiptId, BlankBinding Blank, ReadOnlyMemory<byte> Image,
    string MediaType, OperationIdentity Operation, string WorkerToken, DateTimeOffset LeaseUntil);
public sealed record OutboxDelivery(IntakeEvent Event, string WorkerToken);

public interface ISessionRepository
{
    Task<IntakeSession?> FindAsync(string gameId, CancellationToken cancellationToken);
    Task SaveAsync(IntakeSession session, CancellationToken cancellationToken);
}
public interface IReceiptRepository
{
    Task<ImageReference?> ImageAsync(string receiptId, CancellationToken cancellationToken);
    Task<Receipt?> FindAsync(string receiptId, CancellationToken cancellationToken);
    Task<bool> ExistsForBlankAsync(string blankId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListIdsAsync(string gameId, CancellationToken cancellationToken);
    Task AddAsync(Receipt receipt, ReadOnlyMemory<byte> verifiedImage, string mediaType, CancellationToken cancellationToken);
}
public interface ISubmissionRepository
{
    Task<PendingSubmission?> FindForOperationAsync(OperationIdentity identity, CancellationToken cancellationToken);
    Task<bool> ExistsForBlankAsync(string blankId, CancellationToken cancellationToken);
    Task AddAsync(PendingSubmission pending, CancellationToken cancellationToken);
    Task<bool> IsOwnedAsync(string receiptId, string workerToken, CancellationToken cancellationToken);
    Task RemoveAsync(string receiptId, string workerToken, CancellationToken cancellationToken);
    Task<PendingSubmission?> ClaimExpiredAsync(string workerToken, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken);
}
public interface IOperationRepository
{
    Task<SavedOperation?> FindAsync(OperationIdentity identity, CancellationToken cancellationToken);
    Task SaveAsync(OperationIdentity identity, OperationResult result, CancellationToken cancellationToken);
}
public interface IOutboxRepository
{
    Task AddAsync(IntakeEvent message, CancellationToken cancellationToken);
    Task<OutboxDelivery?> ClaimAsync(string workerToken, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken);
    Task CompleteAsync(string eventId, string workerToken, CancellationToken cancellationToken);
    Task RetryAsync(string eventId, string workerToken, DateTimeOffset retryAt, CancellationToken cancellationToken);
}
public interface IIntakeTransaction
{
    ISessionRepository Sessions { get; }
    IReceiptRepository Receipts { get; }
    ISubmissionRepository Submissions { get; }
    IOperationRepository Operations { get; }
    IOutboxRepository Outbox { get; }
}
public interface IIntakeUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<IIntakeTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}
