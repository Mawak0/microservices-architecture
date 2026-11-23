using System.Text.Json;
using Microsoft.AspNetCore.Routing;
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
            ResolveGame = ResolveGameAsync,
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

    private static async Task<string?> ResolveGameAsync(HttpContext context, JsonElement? body)
    {
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var id = pattern == "/appeals"
            ? OperationGame.Identifier(body, "checkId")
            : context.Request.RouteValues["id"] as string;
        if (id is null)
            return pattern == "/appeals" ? null : OperationGame.FromRequest(context, body);

        var appeal = pattern?.StartsWith("/appeals/{id}/", StringComparison.Ordinal) == true;
        var transactions = context.RequestServices.GetRequiredService<ITransactions>();
        var result = await transactions.Run<string?>(null, session => new(appeal
            ? session.Appeals.Find(id)?.GameId
            : session.Checks.Find(id)?.Receipt.GameId), context.RequestAborted);
        return result.Value;
    }
}
