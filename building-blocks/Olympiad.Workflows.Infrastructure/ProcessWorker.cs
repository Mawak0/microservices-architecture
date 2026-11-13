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

public sealed class ProcessWorker(Coordinator coordinator,IEnumerable<IProcessProbe> probes,IHostApplicationLifetime lifetime,ILogger<ProcessWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!lifetime.ApplicationStarted.IsCancellationRequested) await Task.Delay(50,ct);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                foreach(var probe in probes) await probe.PollAsync(ct);
                if(await coordinator.RunAsync(null,Guid.NewGuid().ToString(),ct)) continue;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { return; }
            catch(Exception exception) { logger.LogError(exception,"Saved process remains recoverable after worker failure"); }
            await Task.Delay(250,ct);
        }
    }
}
