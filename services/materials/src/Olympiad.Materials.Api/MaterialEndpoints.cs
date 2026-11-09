using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Api;

internal static class MaterialEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/kits", async (HttpContext http, PrepareKit handler) =>
        {
            http.Actor().Require(MaterialPermission.Prepare);
            var request = await Transport.ReadAsync<KitRequest>(http);
            var kit = await handler.ExecuteAsync(http.Command(), new(Transport.Required(request.GameId), Transport.Required(request.TeamId)), http.RequestAborted);
            return Results.Json(Transport.Kit(kit), statusCode: 201);
        });
        app.MapGet("/kits/{id}", async (HttpContext http, string id, MaterialQueries queries) => Results.Ok(Transport.Kit(await queries.KitAsync(http.Actor(), id, http.RequestAborted))));
        app.MapPost("/blanks", async (HttpContext http, IssueBlanks handler) =>
        {
            http.Actor().Require(MaterialPermission.Issue);
            var request = await Transport.ReadAsync<IssueRequest>(http);
            var blanks = await handler.ExecuteAsync(http.Command(), new(Transport.Required(request.GameId), Transport.Required(request.TeamId),
                Transport.Required(request.TaskId), request.Count, Transport.Modifier(request.Modifier)), http.RequestAborted);
            return Results.Json(new { blanks = blanks.Select(Transport.Blank) }, statusCode: 201);
        });
        app.MapGet("/blanks/{id}", async (HttpContext http, string id, MaterialQueries queries) => Results.Ok(Transport.Blank(await queries.BlankAsync(http.Actor(), id, http.RequestAborted))));
        app.MapPost("/blank-verifications", async (HttpContext http, MaterialQueries queries) =>
        {
            http.Actor().Require(MaterialPermission.Verify);
            var request = await Transport.ReadAsync<VerificationRequest>(http);
            return Results.Ok(Transport.Blank(await queries.VerifyAsync(http.Actor(), Transport.Required(request.QrToken), http.RequestAborted)));
        });
        app.MapPost("/print-jobs", async (HttpContext http, CreatePrintJob handler) =>
        {
            http.Actor().Require(MaterialPermission.Print);
            var request = await Transport.ReadAsync<PrintRequest>(http);
            if (request.BlankIds is null || request.TaskIds is null) throw new DomainException("VALIDATION_ERROR", "Both material arrays are required.");
            foreach (var id in request.BlankIds.Concat(request.TaskIds)) Transport.Required(id);
            if (request.TeamId is not null) Transport.Required(request.TeamId);
            var job = await handler.ExecuteAsync(http.Command(), new(Transport.Required(request.GameId), request.TeamId, request.BlankIds, request.TaskIds), http.RequestAborted);
            return Results.Json(Transport.Job(job), statusCode: 202);
        });
        app.MapGet("/print-jobs/{id}", async (HttpContext http, string id, MaterialQueries queries) => Results.Ok(Transport.Job(await queries.PrintJobAsync(http.Actor(), id, http.RequestAborted))));
        app.MapGet("/print-jobs/{id}/artifact", async (HttpContext http, string id, MaterialQueries queries) =>
            Results.File(await queries.ArtifactAsync(http.Actor(), id, http.RequestAborted), "application/pdf", $"olympiad-{id}.pdf"));
    }
}
