using System.Text.Json;
using Olympiad.Review.Application;
using Olympiad.Review.Domain;
using Olympiad.Workflows.Infrastructure;

namespace Olympiad.Review.Api;

internal static class WorkflowConfiguration
{
    public static void AddReviewWorkflows(this WebApplicationBuilder builder)
    {
        if(!builder.Configuration.GetValue("Workflows:Enabled",true)) return;
        builder.Services.AddWorkflows(builder.Configuration["Storage:ConnectionString"]!,new()
        {
            CaptureActor=c=> { var actor=(AuthenticatedActor)c.Items["actor"]!; return ActorSnapshots.Capture(actor.Id,actor.Roles); },
            RestoreActor=(c,json)=> { var saved=ActorSnapshots.Read(json); c.Items["actor"]=new AuthenticatedActor(saved.Id,saved.Roles); },
            Track=(method,path)=>method is "POST" or "PUT" or "PATCH" or "DELETE" && !path.StartsWith("/integrations/",StringComparison.Ordinal),
            ProtectStatusEndpoint=route=>route.WithMetadata(new Roles("observer","checker","organizer","integration")),
            ClassifyError=exception=>exception switch
            {
                DomainFailure failure=>new(failure.Kind switch {FailureKind.Validation=>422,FailureKind.Missing=>404,FailureKind.Dependency=>503,_=>409},failure.Code,failure.Code),
                JsonException or BadHttpRequestException=>new(422,"VALIDATION_ERROR","Invalid JSON request."),
                OperationCanceledException=>new(503,"DEPENDENCY_UNAVAILABLE","The current attempt timed out."),
                _=>new(500,"INTERNAL_ERROR","The saved request will be retried.")
            },
            Lease=TimeSpan.FromSeconds(builder.Configuration.GetValue("Workflows:LeaseSeconds",60)),
            CheckpointTimeout=TimeSpan.FromSeconds(builder.Configuration.GetValue("Workflows:CheckpointTimeoutSeconds",30)),
            RetryBase=TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Workflows:RetryBaseMilliseconds",1000))
        });
    }
}
