using System.Text.Json;
using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;
using Olympiad.Workflows.Infrastructure;
using Olympiad.Storage.S3;

namespace Olympiad.Intake.Api;

internal static class WorkflowConfiguration
{
    public static void AddIntakeWorkflows(this WebApplicationBuilder builder,IIntakeUnitOfWork store,S3Objects? images)
    {
        if(!builder.Configuration.GetValue("Workflows:Enabled",true)) return;
        builder.Services.AddWorkflows(builder.Configuration["Storage:ConnectionString"]!,new()
        {
            CaptureActor=c=> { var actor=(Actor)c.Items[typeof(Actor)]!; return ActorSnapshots.Capture(actor.UserId,actor.Roles); },
            RestoreActor=(c,json)=> { var saved=ActorSnapshots.Read(json); c.Items[typeof(Actor)]=new Actor(saved.Id,saved.Roles.ToHashSet(StringComparer.Ordinal)); },
            Checkpoint=path=>path=="/receipts"?"ReviewRegistered":null,
            ReplayCode=(path,code)=>path=="/receipts"&&code==201?200:code,
            ProtectStatusEndpoint=route=>route.WithMetadata(new RequiredRoles("observer","checker","organizer","integration")),
            ClassifyError=Classify,
            Compensate=async (process,ct)=>
            {
                if(images is null || process.Result?.ResourceId is not { } resource) return;
                var accepted=await store.ExecuteAsync((tx,token)=>tx.Receipts.FindAsync(resource,token),ct);
                if(accepted is not null) throw new InvalidOperationException("An accepted answer cannot be compensated.");
                await images.DeleteOwnerAsync(resource,ct);
            },
            Lease=TimeSpan.FromSeconds(builder.Configuration.GetValue("Workflows:LeaseSeconds",60)),
            CheckpointTimeout=TimeSpan.FromSeconds(builder.Configuration.GetValue("Workflows:CheckpointTimeoutSeconds",30)),
            RetryBase=TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Workflows:RetryBaseMilliseconds",1000))
        });
    }
    private static HttpFailure Classify(Exception exception)
    {
        if(exception is IntakeException failure)
        { var mapped=Errors.Map(failure.Error); return new(mapped.Status,mapped.Code,failure.Message,failure.CompensationResourceId); }
        return exception switch
        {
            JsonException or BadHttpRequestException=>new(422,"VALIDATION_ERROR","Invalid JSON request."),
            OperationCanceledException=>new(503,"DEPENDENCY_UNAVAILABLE","The current attempt timed out."),
            _=>new(503,"STORAGE_UNAVAILABLE","The saved request will be retried.")
        };
    }
}
