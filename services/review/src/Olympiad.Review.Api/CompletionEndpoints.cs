using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Api;
internal static class CompletionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/games/{gameId}/completion", async (string gameId, HttpContext c, ReviewCompletion handler) =>
            Results.Json(await handler.Get(gameId, c.RequestAborted))).WithMetadata(new Roles("integration"));
        app.MapPost("/integrations/intake-closures", async (HttpContext c, ReviewCompletion handler) =>
        {
            var request = await Transport.Body<Incoming<ClosureData>>(c);
            if (request.Type != "IntakeClosed" || request.AggregateId != request.Data.GameId ||
                request.Data.AdmissionVersion < 1 || request.Data.ReceiptIds is null)
                throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
            return Transport.Reply(await handler.Receive(await Transport.Identity(c),
                Required.Text(request.Data.GameId), request.Data.AdmissionVersion,
                request.Data.ReceiptIds, c.RequestAborted));
        }).WithMetadata(new Roles("integration"));
    }
}
