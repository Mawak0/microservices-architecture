using Sapientum.Review.Application;
using Sapientum.Review.Domain;
namespace Sapientum.Review.Api;
internal static class AppealEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/appeals", async (HttpContext c, OpenAppeal handler) =>
        {
            var request = await Transport.Body<AppealRequest>(c);
            return Transport.Reply(await handler.Execute(await Transport.Identity(c),
                Required.Text(request.CheckId), Required.Text(request.Reason), c.RequestAborted));
        }).WithMetadata(new Roles("observer"));
        app.MapGet("/appeals/{id}", async (string id, HttpContext c, AppealAssignments handler) =>
            Results.Json(await handler.Get(id, c.RequestAborted))).WithMetadata(new Roles("checker", "integration"));
        app.MapPost("/appeals/next", async (HttpContext c, AppealAssignments handler) =>
        {
            var request = await Transport.Body<GameRequest>(c);
            return Transport.Reply(await handler.Next(await Transport.Identity(c),
                Required.Text(request.GameId), c.RequestAborted));
        }).WithMetadata(new Roles("checker"));
        app.MapPost("/appeals/{id}/claim", async (string id, HttpContext c, AppealAssignments handler) =>
            Transport.Reply(await handler.Claim(await Transport.Identity(c), id, c.RequestAborted)))
            .WithMetadata(new Roles("checker"));
        app.MapPost("/appeals/{id}/renew", async (string id, HttpContext c, AppealAssignments handler) =>
            Transport.Reply(await handler.Renew(await Transport.Identity(c), id, Transport.Token(c), c.RequestAborted)))
            .WithMetadata(new Roles("checker"));
        app.MapPost("/appeals/{id}/release", async (string id, HttpContext c, AppealAssignments handler) =>
            Transport.Reply(await handler.Release(await Transport.Identity(c), id, Transport.Token(c),
                Transport.MayReleaseOtherOwner(c), c.RequestAborted)))
            .WithMetadata(new Roles("checker"));
        app.MapPost("/appeals/{id}/resolve", async (string id, HttpContext c, ResolveAppeal handler) =>
        {
            var request = await Transport.Body<ResolutionRequest>(c);
            return Transport.Reply(await handler.Execute(await Transport.Identity(c), id, Transport.Token(c),
                request.Outcome, request.Verdict is null ? null : Verdicts.Parse(request.Verdict),
                Required.Text(request.Reason), c.RequestAborted));
        }).WithMetadata(new Roles("checker"));
        app.MapGet("/appeal-quotas/{gameId}", async (string gameId, HttpContext c, QuotaCommands handler) =>
            Results.Json(await handler.Get(gameId, c.RequestAborted))).WithMetadata(new Roles("checker", "observer", "integration"));
        app.MapPut("/appeal-quotas/{gameId}", async (string gameId, HttpContext c, QuotaCommands handler) =>
        {
            var request = await Transport.Body<LimitRequest>(c);
            return Transport.Reply(await handler.Set(await Transport.Identity(c), gameId, request.Limit, c.RequestAborted));
        }).WithMetadata(new Roles("admin"));
    }
}
