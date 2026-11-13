namespace Olympiad.Workflows.Domain;

public enum ProcessStatus { Accepted, Running, AwaitingCheckpoint, Compensating, Completed, Failed }
public sealed record Invocation(string Handler, string Method, string Path, string RouteValues, string Headers, string Actor, byte[] Arguments);
public sealed record ProcessResult(int StatusCode, string ContentType, byte[] Body, string? ResourceId = null);
public sealed record ProcessSnapshot(string Id, string ActorId, string Scope, string Key, string Fingerprint,
    Invocation? Invocation, ProcessStatus Status, int Attempts, DateTimeOffset NextAttempt,
    string? LeaseToken, DateTimeOffset? LeaseUntil, string? Checkpoint, ProcessResult? Result, string? Error,
    DateTimeOffset CreatedAt, int Version = 1);

/// <summary>A durable command is removed from active work only by a committed terminal transition.</summary>
public sealed class Process
{
    private ProcessSnapshot state;
    private Process(ProcessSnapshot state) => this.state = Copy(state);
    public ProcessSnapshot Snapshot => Copy(state);
    private static ProcessSnapshot Copy(ProcessSnapshot value) => value with
    {
        Invocation = value.Invocation is { } input ? input with { Arguments = input.Arguments.ToArray() } : null,
        Result = value.Result is { } result ? result with { Body = result.Body.ToArray() } : null
    };
    public static Process Restore(ProcessSnapshot state) => new(state);
    public static Process Accept(string id, string actorId, string scope, string key, string fingerprint,
        Invocation invocation, string? checkpoint, DateTimeOffset now)
    {
        if (new[] { id, actorId, scope, key, fingerprint, invocation.Handler }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A command needs stable identity, actor, arguments and handler.");
        return new(new(id, actorId, scope, key, fingerprint, invocation, ProcessStatus.Accepted, 0, now, null, null, checkpoint, null, null, now));
    }
    public bool IsTerminal => state.Status is ProcessStatus.Completed or ProcessStatus.Failed;
    public bool TryClaim(string token, DateTimeOffset now, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A positive fenced lease is required.");
        if (IsTerminal || state.NextAttempt > now || state.LeaseUntil > now) return false;
        state = state with { Status = state.Status == ProcessStatus.Compensating ? ProcessStatus.Compensating : ProcessStatus.Running,
            LeaseToken = token, LeaseUntil = now + duration, Attempts = checked(state.Attempts + 1) };
        return true;
    }
    public void Succeed(string token, ProcessResult result, bool checkpointConfirmed, DateTimeOffset now, TimeSpan checkpointTimeout)
    {
        RequireOwner(token, now);
        var terminal = state.Checkpoint is null || checkpointConfirmed;
        state = state with { Status = terminal ? ProcessStatus.Completed : ProcessStatus.AwaitingCheckpoint,
            Result = CopyResult(result), Invocation = terminal ? null : state.Invocation, Error = null,
            LeaseToken = null, LeaseUntil = null, NextAttempt = now + checkpointTimeout };
    }
    public void Retry(string token, string error, DateTimeOffset now, TimeSpan delay)
    {
        RequireOwner(token, now);
        state = state with { Status = state.Status == ProcessStatus.Compensating ? ProcessStatus.Compensating : ProcessStatus.Accepted,
            Error = error, LeaseToken = null, LeaseUntil = null, NextAttempt = now + delay };
    }
    public void Reject(string token, ProcessResult result, string error, bool compensate, DateTimeOffset now)
    {
        RequireOwner(token, now);
        state = state with { Status = compensate ? ProcessStatus.Compensating : ProcessStatus.Failed,
            Result = CopyResult(result), Error = error, Invocation = compensate ? state.Invocation : null,
            LeaseToken = null, LeaseUntil = null, NextAttempt = now };
    }
    public void Compensated(string token, DateTimeOffset now)
    {
        RequireOwner(token, now);
        if (state.Status != ProcessStatus.Compensating) throw new InvalidOperationException("The process has no pending compensation.");
        state = state with { Status = ProcessStatus.Failed, Invocation = null, LeaseToken = null, LeaseUntil = null };
    }
    public bool Confirm(string checkpoint, string resourceId)
    {
        if (IsTerminal || state.Checkpoint != checkpoint || state.Result?.ResourceId != resourceId) return false;
        state = state with { Status = ProcessStatus.Completed, Invocation = null, Error = null, LeaseToken = null, LeaseUntil = null };
        return true;
    }
    public bool RejectConfirmation(string checkpoint,string resourceId,string error,ProcessResult result)
    {
        if(IsTerminal||state.Checkpoint!=checkpoint||state.Result?.ResourceId!=resourceId) return false;
        state=state with {Status=ProcessStatus.Failed,Invocation=null,Result=CopyResult(result),Error=error,LeaseToken=null,LeaseUntil=null};
        return true;
    }
    private static ProcessResult CopyResult(ProcessResult value) => value with { Body=value.Body.ToArray() };
    private void RequireOwner(string token, DateTimeOffset now)
    {
        if (state.LeaseToken != token || state.LeaseUntil <= now || IsTerminal)
            throw new InvalidOperationException("The process worker lost its lease.");
    }
}
