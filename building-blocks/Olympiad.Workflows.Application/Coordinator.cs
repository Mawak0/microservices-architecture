using Olympiad.Workflows.Domain;

namespace Olympiad.Workflows.Application;

public sealed record Execution(ProcessResult Result, bool Retryable = false, string? Error = null, bool Compensate = false);
public interface ICommandExecutor { Task<Execution> ExecuteAsync(ProcessSnapshot process, CancellationToken cancellationToken); }
public interface ICommandCompensator { Task ExecuteAsync(ProcessSnapshot process, CancellationToken cancellationToken); }
public interface IProcessClock { DateTimeOffset UtcNow { get; } }
public interface IRetrySchedule { TimeSpan Delay(int attempt); }
public interface IProcessMonitor { void Attempt(ProcessSnapshot process); }
public interface IProcessProbe { Task PollAsync(CancellationToken cancellationToken); }
public interface IProcessStore
{
    Task<ProcessSnapshot> AcceptAsync(Process process, CancellationToken cancellationToken);
    Task<ProcessSnapshot?> FindAsync(string id, string actorId, CancellationToken cancellationToken);
    Task<ProcessSnapshot?> ClaimAsync(string? id, string token, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    Task FinishAsync(string id, string token, Execution execution, DateTimeOffset now, TimeSpan retry, TimeSpan checkpointTimeout, CancellationToken cancellationToken);
    Task CompensatedAsync(string id, string token, DateTimeOffset now, CancellationToken cancellationToken);
    Task ConfirmAsync(string checkpoint, string resourceId, CancellationToken cancellationToken);
    Task ConfirmOperationAsync(string operationId, int version, string checkpoint, string resourceId, CancellationToken cancellationToken);
    Task RejectCheckpointAsync(string checkpoint, string resourceId, string error, CancellationToken cancellationToken);
}
public sealed record ProcessTiming(TimeSpan Lease, TimeSpan CheckpointTimeout, int AlertAfterAttempts = 10);

public sealed class Coordinator(IProcessStore store, ICommandExecutor executor, ICommandCompensator compensator,
    IProcessClock clock, IRetrySchedule schedule, ProcessTiming timing, IProcessMonitor monitor)
{
    public async Task<bool> RunAsync(string? id, string workerToken, CancellationToken ct)
    {
        var work = await store.ClaimAsync(id, workerToken, clock.UtcNow, timing.Lease, ct);
        if (work is null) return false;
        monitor.Attempt(work);
        if (work.Status == ProcessStatus.Compensating)
        {
            try
            {
                await compensator.ExecuteAsync(work, ct);
                await store.CompensatedAsync(work.Id, workerToken, clock.UtcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                await store.FinishAsync(work.Id, workerToken, new(new(503,"application/json",[]),true,"COMPENSATION_UNAVAILABLE"),
                    clock.UtcNow,schedule.Delay(work.Attempts),timing.CheckpointTimeout,ct);
            }
            return true;
        }
        var result = await executor.ExecuteAsync(work, ct);
        await store.FinishAsync(work.Id, workerToken, result, clock.UtcNow, schedule.Delay(work.Attempts), timing.CheckpointTimeout, ct);
        return true;
    }
}
