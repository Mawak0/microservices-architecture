namespace Sapientum.Materials.Domain;

public enum PrintStatus { Pending, Running, Completed, Failed }
public sealed record PrintFailure(string Code, string Message);
public sealed record ManifestEntry(string Kind, string Id);

public sealed class PrintJob
{
    public string Id { get; }
    public string GameId { get; }
    public string? TeamId { get; }
    public PrintSnapshot Snapshot { get; }
    public PrintStatus Status { get; private set; }
    public string? ArtifactUrl { get; private set; }
    public PrintFailure? Error { get; private set; }
    public string? ProcessingToken { get; private set; }
    public DateTimeOffset? ProcessingExpiresAt { get; private set; }
    public IReadOnlyList<ManifestEntry> Manifest => Snapshot.Blanks.Select(b => new ManifestEntry("answer-blank", b.Blank.Id))
        .Concat(Snapshot.Tasks.Select(t => new ManifestEntry("task-statement", t.Id))).ToArray();

    public PrintJob(string id, string gameId, string? teamId, PrintSnapshot snapshot,
        PrintStatus status = PrintStatus.Pending, string? artifactUrl = null, PrintFailure? error = null,
        string? processingToken = null, DateTimeOffset? processingExpiresAt = null)
    {
        Id = Guard.Id(id, nameof(id)); GameId = Guard.Id(gameId, nameof(gameId)); TeamId = teamId; Snapshot = snapshot;
        foreach (var b in Snapshot.Blanks) b.Blank.EnsurePrintableIn(gameId, teamId);
        if (!Enum.IsDefined(status) || (status == PrintStatus.Completed && string.IsNullOrWhiteSpace(artifactUrl)) ||
            (status == PrintStatus.Running && (string.IsNullOrWhiteSpace(processingToken) || processingExpiresAt is null)) ||
            (status == PrintStatus.Failed && error is null))
            throw new DomainException("VALIDATION_ERROR", "The restored print job has an inconsistent state.");
        Status = status; ArtifactUrl = artifactUrl; Error = error; ProcessingToken = processingToken; ProcessingExpiresAt = processingExpiresAt;
    }

    public bool TryStart(string token, DateTimeOffset now, TimeSpan lease)
    {
        if (Status != PrintStatus.Pending && !(Status == PrintStatus.Running && ProcessingExpiresAt <= now)) return false;
        if (lease <= TimeSpan.Zero) throw new DomainException("VALIDATION_ERROR", "Processing lease must be positive.");
        Status = PrintStatus.Running; ProcessingToken = Guard.Id(token, nameof(token)); ProcessingExpiresAt = now + lease;
        return true;
    }

    public void Complete(string token, string artifactUrl)
    {
        EnsureOwnership(token); Status = PrintStatus.Completed; ArtifactUrl = Guard.Id(artifactUrl, nameof(artifactUrl));
        ProcessingToken = null; ProcessingExpiresAt = null;
    }
    public void Fail(string token, PrintFailure failure)
    {
        EnsureOwnership(token); Status = PrintStatus.Failed; Error = failure; ProcessingToken = null; ProcessingExpiresAt = null;
    }
    private void EnsureOwnership(string token)
    {
        if (Status != PrintStatus.Running || token != ProcessingToken)
            throw new DomainException("PRINT_ASSIGNMENT_STALE", "This print attempt no longer owns the job.");
    }
}
