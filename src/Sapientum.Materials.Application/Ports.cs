using Sapientum.Materials.Domain;

namespace Sapientum.Materials.Application;

public interface IGameMaterialsGateway { Task<GameMaterials> GetAsync(string id, CancellationToken cancellationToken); }
public interface ITeamAdmissionGateway { Task<TeamAdmission> GetAsync(string id, CancellationToken cancellationToken); }
public interface ITaskStatementGateway { Task<string> GetStatementAsync(string taskId, CancellationToken cancellationToken); }
public interface ITemplateRepository { Task<MaterialTemplate> GetAsync(string key, CancellationToken cancellationToken); }
public interface IBlankTokenProtector
{
    string Protect(BlankIdentity identity);
    BlankIdentity? Unprotect(string token);
}
public interface IClock { Task<DateTimeOffset> UtcNowAsync(CancellationToken cancellationToken); }
public interface IPdfRenderer { Task<byte[]> RenderAsync(PrintSnapshot snapshot, CancellationToken cancellationToken); }

public interface IBlankRepository
{
    Task<long> ReserveNumbersAsync(int count, CancellationToken cancellationToken);
    Task AddAsync(AnswerBlank blank, CancellationToken cancellationToken);
    Task<AnswerBlank?> FindAsync(string id, CancellationToken cancellationToken);
    Task<AnswerBlank?> FindByTokenAsync(string token, CancellationToken cancellationToken);
}
public interface IKitRepository
{
    Task<TeamKit?> FindAsync(string id, CancellationToken cancellationToken);
    Task<TeamKit?> FindForTeamAsync(string gameId, string teamId, CancellationToken cancellationToken);
    Task AddAsync(TeamKit kit, CancellationToken cancellationToken);
}
public interface IPrintJobRepository
{
    Task AddAsync(PrintJob job, CancellationToken cancellationToken);
    Task<PrintJob?> FindAsync(string id, CancellationToken cancellationToken);
    Task<PrintJob?> FindNextPendingAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task SaveAsync(PrintJob job, CancellationToken cancellationToken);
    Task StoreArtifactAsync(string jobId, byte[] artifact, CancellationToken cancellationToken);
    Task<byte[]?> FindArtifactAsync(string jobId, CancellationToken cancellationToken);
}
public sealed record IdempotencyRecord(string Scope, string Key, string Fingerprint, IReadOnlyList<string> ResourceIds);
public interface IIdempotencyRepository
{
    Task<IdempotencyRecord?> FindAsync(string scope, string key, CancellationToken cancellationToken);
    Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken);
}

public sealed record MaterialEvent(string EventId, string Type, DateTimeOffset OccurredAt, string AggregateId,
    string GameId, string? TeamId, string? KitId = null, IReadOnlyList<string>? BlankIds = null,
    string? PrintJobId = null, string? ArtifactUrl = null, string? TemplateKey = null);
public interface IOutboxRepository
{
    Task AddAsync(MaterialEvent message, CancellationToken cancellationToken);
    Task<IReadOnlyList<MaterialEvent>> GetPendingAsync(int limit, CancellationToken cancellationToken);
    Task MarkDeliveredAsync(string eventId, CancellationToken cancellationToken);
}
public interface IEventPublisher { Task PublishAsync(MaterialEvent message, CancellationToken cancellationToken); }

/// <summary>One short atomic unit. No gateway or renderer calls run inside this boundary.</summary>
public interface IMaterialsSession
{
    IBlankRepository Blanks { get; }
    IKitRepository Kits { get; }
    IPrintJobRepository PrintJobs { get; }
    IIdempotencyRepository Idempotency { get; }
    IOutboxRepository Outbox { get; }
}
public interface IMaterialsTransactions
{
    Task<T> ExecuteAsync<T>(Func<IMaterialsSession, CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}
