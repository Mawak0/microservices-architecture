using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Olympiad.Persistence.Postgres;
using Olympiad.Workflows.Application;
using Olympiad.Workflows.Domain;

namespace Olympiad.Workflows.Infrastructure;

public sealed record ProcessActor(string Id, string Snapshot, bool Administrator);
public sealed record SavedActor(string Id, string[] Roles);
public static class ActorSnapshots
{
    public static ProcessActor Capture(string id,IEnumerable<string> roles)
    {
        var snapshot=new SavedActor(id,roles.ToArray());
        return new(id,JsonSerializer.Serialize(snapshot),snapshot.Roles.Contains("admin"));
    }
    public static SavedActor Read(string snapshot) => JsonSerializer.Deserialize<SavedActor>(snapshot) ?? throw new InvalidOperationException("Saved actor is missing.");
}
public sealed record HttpFailure(int Status, string Code, string Message, string? CompensationResource = null);
public sealed class HttpWorkflowOptions
{
    public required Func<HttpContext, ProcessActor> CaptureActor { get; init; }
    public required Action<HttpContext, string> RestoreActor { get; init; }
    public required Func<Exception, HttpFailure> ClassifyError { get; init; }
    public Action<HttpContext> Authorize { get; init; } = _ => { };
    public Func<string, string, bool> Track { get; init; } = (method, _) => method is "POST" or "PUT" or "PATCH" or "DELETE";
    public Func<string, string?> Checkpoint { get; init; } = _ => null;
    public Func<string, int, int> ReplayCode { get; init; } = (_, code) => code;
    public Func<ProcessSnapshot, CancellationToken, Task> Compensate { get; init; } = (_, _) => Task.CompletedTask;
    public Action<RouteHandlerBuilder> ProtectStatusEndpoint { get; init; } = _ => { };
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CheckpointTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryMaximum { get; init; } = TimeSpan.FromMinutes(2);
    public int AlertAfterAttempts { get; init; } = 10;
}
public sealed class ProcessClock(TimeProvider time) : IProcessClock { public DateTimeOffset UtcNow => time.GetUtcNow(); }
public sealed class ExponentialRetry(HttpWorkflowOptions options) : IRetrySchedule
{
    public TimeSpan Delay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(options.RetryMaximum.TotalMilliseconds,
        options.RetryBase.TotalMilliseconds * Math.Pow(2,Math.Min(Math.Max(attempt-1,0),20))) * (0.8 + Random.Shared.NextDouble()*0.2));
}
public sealed class DelegateCompensator(HttpWorkflowOptions options) : ICommandCompensator
{ public Task ExecuteAsync(ProcessSnapshot process,CancellationToken ct) => options.Compensate(process,ct); }
public sealed class ProcessMonitor(ProcessTiming timing,ILogger<ProcessMonitor> logger) : IProcessMonitor
{
    public void Attempt(ProcessSnapshot process)
    {
        if(process.Attempts>=timing.AlertAfterAttempts)
            logger.LogWarning("Process {ProcessId} needs attention: attempt {Attempt}, last error {Error}; retries remain enabled",process.Id,process.Attempts,process.Error);
    }
}
