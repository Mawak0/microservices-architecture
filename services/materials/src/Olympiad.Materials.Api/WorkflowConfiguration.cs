using System.Text.Json;
using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;
using Olympiad.Workflows.Infrastructure;

namespace Olympiad.Materials.Api;

internal static class WorkflowConfiguration
{
    public static void AddMaterialWorkflows(this WebApplicationBuilder builder)
    {
        if(!builder.Configuration.GetValue("Workflows:Enabled",true)) return;
        builder.Services.AddWorkflows(builder.Configuration["Storage:ConnectionString"]!,new()
        {
            CaptureActor=c=>ActorSnapshots.Capture(c.Actor().Id,c.Actor().RoleNames),
            RestoreActor=(c,json)=> { var saved=ActorSnapshots.Read(json); c.Items[typeof(Actor)]=new Actor(saved.Id,saved.Roles); },
            Authorize=c=>c.Actor().Require(c.Request.Path.Value switch { "/kits"=>MaterialPermission.Prepare,"/blanks"=>MaterialPermission.Issue,_=>MaterialPermission.Print }),
            Track=(method,path)=>method is "POST" or "PUT" or "PATCH" or "DELETE" && path!="/blank-verifications",
            Checkpoint=path=>path=="/print-jobs"?"PdfReady":null,
            ClassifyError=exception=>exception switch
            {
                UnauthenticatedException=>new(401,"UNAUTHENTICATED",exception.Message),
                AccessDeniedException=>new(403,"FORBIDDEN",exception.Message),
                NotFoundException=>new(404,"NOT_FOUND",exception.Message),
                DependencyUnavailableException=>new(503,"DEPENDENCY_UNAVAILABLE",exception.Message),
                JsonException or BadHttpRequestException=>new(422,"VALIDATION_ERROR","Invalid JSON request."),
                DomainException error=>new(error.Code is "TEAM_NOT_ADMITTED" or "IDEMPOTENCY_KEY_REUSED" or "PRINT_ASSIGNMENT_STALE"?409:422,error.Code,error.Message),
                OperationCanceledException=>new(503,"DEPENDENCY_UNAVAILABLE","The current attempt timed out."),
                _=>new(500,"INTERNAL_ERROR","The saved request will be retried.")
            },
            Lease=TimeSpan.FromSeconds(builder.Configuration.GetValue("Workflows:LeaseSeconds",60)),
            CheckpointTimeout=TimeSpan.FromSeconds(builder.Configuration.GetValue("Workflows:CheckpointTimeoutSeconds",30)),
            RetryBase=TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Workflows:RetryBaseMilliseconds",1000))
        });
    }
}
