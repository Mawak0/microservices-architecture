using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Tests;

internal static class Sample
{
    public static GameMaterials Game(bool modifiers = true, string key = "layout/v1") => new("game", modifiers, 3, 2, false, key,
        [new("task-1", "1"), new("task-2", "2")], new Dictionary<string, TemplateValue> { ["heading"] = new(TemplateValueKind.Text, "Математическая олимпиада") });
    public static MaterialTemplate Template(string key = "layout/v1") => new(key,
        [new(TemplateBlockKind.Field, Field: TemplateField.Heading), new(TemplateBlockKind.Field, Field: TemplateField.TaskNumber), new(TemplateBlockKind.Qr), new(TemplateBlockKind.AnswerArea)],
        [new(TemplateBlockKind.Field, Field: TemplateField.Statement)]);
    public static AnswerBlank Blank(string id = "blank", long number = 1, string qr = "qr") => new(id, number, "game", "team", "task-1", "1", qr, null);
    public static PrintSnapshot Snapshot(string statement = "Условие: вычислите 2 + 2") => new(Template(), Sample.Game().TemplateParameters,
        [new(Blank(), "Команда Альфа")], [new("task-1", "1", statement)]);
    public static CommandContext Admin(string key = "key") => new(new("admin", ["admin"]), key);
}

internal sealed class StubDependencies : IGameMaterialsGateway, ITeamAdmissionGateway, ITaskStatementGateway, ITemplateRepository, IClock
{
    public bool Unavailable { get; set; }
    public bool Admitted { get; set; } = true;
    public int Calls { get; private set; }
    public GameMaterials Game { get; set; } = Sample.Game();
    public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private void Check() { Calls++; if (Unavailable) throw new DependencyUnavailableException("offline"); }
    Task<GameMaterials> IGameMaterialsGateway.GetAsync(string id, CancellationToken cancellationToken) { Check(); return Task.FromResult(Game); }
    Task<TeamAdmission> ITeamAdmissionGateway.GetAsync(string id, CancellationToken cancellationToken) { Check(); return Task.FromResult(new TeamAdmission(id, Game.Id, Admitted, "Команда")); }
    public Task<string> GetStatementAsync(string taskId, CancellationToken cancellationToken) { Check(); return Task.FromResult("Условие задачи"); }
    Task<MaterialTemplate> ITemplateRepository.GetAsync(string key, CancellationToken cancellationToken) { Check(); return Task.FromResult(Sample.Template(key)); }
    public Task<DateTimeOffset> UtcNowAsync(CancellationToken cancellationToken) { Check(); return Task.FromResult(Now); }
}
internal sealed class RecordingPublisher : IEventPublisher
{
    public bool Unavailable { get; set; }
    public List<MaterialEvent> Attempts { get; } = [];
    public Task PublishAsync(MaterialEvent message, CancellationToken cancellationToken)
    {
        Attempts.Add(message);
        if (Unavailable) throw new DependencyUnavailableException("offline");
        return Task.CompletedTask;
    }
}

/// <summary>Substitutable test adapter for exactly the same repository ports as SQLite.</summary>
internal sealed class MemoryTransactions : IMaterialsTransactions, IMaterialsSession, IBlankRepository, IKitRepository,
    IPrintJobRepository, IIdempotencyRepository, IOutboxRepository
{
    private long sequence;
    private Dictionary<string, AnswerBlank> blanks = [];
    private Dictionary<string, TeamKit> kits = [];
    private Dictionary<string, PrintJob> jobs = [];
    private Dictionary<string, PrintArtifact> artifacts = [];
    private Dictionary<(string Scope, string Key), IdempotencyRecord> replays = [];
    private Dictionary<string, MaterialEvent> pending = [];
    public IBlankRepository Blanks => this;
    public IKitRepository Kits => this;
    public IPrintJobRepository PrintJobs => this;
    public IIdempotencyRepository Idempotency => this;
    public IOutboxRepository Outbox => this;
    public async Task<T> ExecuteAsync<T>(Func<IMaterialsSession, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var saved = (sequence, blanks: new Dictionary<string, AnswerBlank>(blanks), kits: new Dictionary<string, TeamKit>(kits),
            jobs: jobs.ToDictionary(p => p.Key, p => Clone(p.Value)), artifacts: new Dictionary<string, PrintArtifact>(artifacts),
            replays: new Dictionary<(string, string), IdempotencyRecord>(replays), pending: new Dictionary<string, MaterialEvent>(pending));
        try { return await action(this, cancellationToken); }
        catch { (sequence, blanks, kits, jobs, artifacts, replays, pending) = saved; throw; }
    }
    private static PrintJob Clone(PrintJob job) => new(job.Id, job.GameId, job.TeamId, job.Snapshot, job.Status, job.ArtifactUrl, job.Error, job.ProcessingToken, job.ProcessingExpiresAt);
    public Task<long> ReserveNumbersAsync(int count, CancellationToken cancellationToken) { var first = sequence + 1; sequence += count; return Task.FromResult(first); }
    public Task AddAsync(AnswerBlank blank, CancellationToken cancellationToken)
    {
        if (blanks.Values.Any(b => b.Number == blank.Number || b.QrToken == blank.QrToken)) throw new InvalidOperationException("Duplicate identity.");
        blanks.Add(blank.Id, blank); return Task.CompletedTask;
    }
    Task<AnswerBlank?> IBlankRepository.FindAsync(string id, CancellationToken cancellationToken) => Task.FromResult(blanks.GetValueOrDefault(id));
    public Task<AnswerBlank?> FindByTokenAsync(string token, CancellationToken cancellationToken) => Task.FromResult(blanks.Values.SingleOrDefault(b => b.QrToken == token));
    Task<TeamKit?> IKitRepository.FindAsync(string id, CancellationToken cancellationToken) => Task.FromResult(kits.GetValueOrDefault(id));
    public Task<TeamKit?> FindForTeamAsync(string gameId, string teamId, CancellationToken cancellationToken) => Task.FromResult(kits.Values.SingleOrDefault(k => k.GameId == gameId && k.TeamId == teamId));
    public Task AddAsync(TeamKit kit, CancellationToken cancellationToken) { kits.Add(kit.Id, kit); return Task.CompletedTask; }
    public Task AddAsync(PrintJob job, CancellationToken cancellationToken) { jobs.Add(job.Id, Clone(job)); return Task.CompletedTask; }
    Task<PrintJob?> IPrintJobRepository.FindAsync(string id, CancellationToken cancellationToken) => Task.FromResult(jobs.TryGetValue(id, out var job) ? Clone(job) : null);
    public Task<PrintJob?> FindNextPendingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var job = jobs.Values.FirstOrDefault(j => j.Status == PrintStatus.Pending || (j.Status == PrintStatus.Running && j.ProcessingExpiresAt <= now));
        return Task.FromResult(job is null ? null : Clone(job));
    }
    public Task SaveAsync(PrintJob job, CancellationToken cancellationToken) { jobs[job.Id] = Clone(job); return Task.CompletedTask; }
    public Task StoreArtifactAsync(string jobId, PrintArtifact artifact, CancellationToken cancellationToken) { artifacts[jobId] = artifact; return Task.CompletedTask; }
    public Task<PrintArtifact?> FindArtifactAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(artifacts.GetValueOrDefault(jobId));
    public Task<IdempotencyRecord?> FindAsync(string scope, string key, CancellationToken cancellationToken) => Task.FromResult(replays.GetValueOrDefault((scope, key)));
    public Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken) { replays.Add((record.Scope, record.Key), record); return Task.CompletedTask; }
    public Task AddAsync(MaterialEvent message, CancellationToken cancellationToken) { pending.Add(message.EventId, message); return Task.CompletedTask; }
    public Task<IReadOnlyList<MaterialEvent>> GetPendingAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MaterialEvent>>(pending.Values.Take(limit).ToArray());
    public Task MarkDeliveredAsync(string eventId, CancellationToken cancellationToken) { pending.Remove(eventId); return Task.CompletedTask; }
}

internal sealed class MemoryArtifacts : IPrintArtifactStorage
{
    private readonly Dictionary<string, byte[]> content = [];
    public bool Unavailable { get; set; }
    public Task<PrintArtifact> SaveAsync(string jobId, string token, byte[] bytes, CancellationToken ct)
    {
        if (Unavailable) throw new DependencyUnavailableException("Storage unavailable");
        var key=jobId+":"+token; content[key]=bytes.ToArray();
        return Task.FromResult(new PrintArtifact(key,"application/pdf",bytes.Length,"checksum"));
    }
    public Task<byte[]> ReadAsync(PrintArtifact artifact,CancellationToken ct) => Task.FromResult(content[artifact.Key]);
}
