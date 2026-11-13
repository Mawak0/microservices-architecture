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

public static class WorkflowSetup
{
    public static void AddWorkflows(this IServiceCollection services,string connectionString,HttpWorkflowOptions options)
    {
        if(options.Lease<=TimeSpan.Zero||options.CheckpointTimeout<=TimeSpan.Zero||options.RetryBase<=TimeSpan.Zero||options.RetryMaximum<options.RetryBase)
            throw new ArgumentException("Configure positive workflow durations and a bounded retry interval.");
        services.AddSingleton(options); services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new PostgresProcesses(new PostgresStore(connectionString)));
        services.AddSingleton<IProcessStore>(p=>p.GetRequiredService<PostgresProcesses>());
        services.AddSingleton<IProcessClock,ProcessClock>(); services.AddSingleton<IRetrySchedule,ExponentialRetry>();
        services.AddSingleton<ICommandExecutor,EndpointExecutor>(); services.AddSingleton<ICommandCompensator,DelegateCompensator>();
        services.AddSingleton<IProcessMonitor,ProcessMonitor>();
        services.AddSingleton(new ProcessTiming(options.Lease,options.CheckpointTimeout,options.AlertAfterAttempts));
        services.AddSingleton<Coordinator>(); services.AddHostedService<ProcessWorker>();
    }
    public static async Task UseWorkflowsAsync(this WebApplication app)
    {
        await app.Services.GetRequiredService<PostgresProcesses>().InitializeAsync(default);
        app.UseMiddleware<SavedRequestMiddleware>();
        var route=app.MapGet("/operations/{id}",async (string id,HttpContext context,IProcessStore store,HttpWorkflowOptions options) =>
        {
            var actor=options.CaptureActor(context);
            var operation=await store.FindAsync(id,actor.Administrator?"*":actor.Id,context.RequestAborted);
            return operation is null?Results.NotFound():Results.Json(SavedRequestMiddleware.View(operation));
        });
        app.Services.GetRequiredService<HttpWorkflowOptions>().ProtectStatusEndpoint(route);
    }
}
