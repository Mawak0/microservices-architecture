using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Api;
internal static class CheckEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/integrations/receipts", async (HttpContext c, ReceiveReceipt handler) =>
        {
            var e = await Transport.Body<Incoming<ReceiptData>>(c);
            if (e.Type != "ReceiptAccepted" || e.AggregateId != e.Data.ReceiptId)
                throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
            return Transport.Reply(await handler.Execute(await Transport.Identity(c),
                Required.Text(e.EventId), e.Data.Receipt(), c.RequestAborted));
        }).WithMetadata(new Roles("integration"));
        app.MapGet("/checks", async (HttpContext c, CheckQueries queries) =>
            Results.Json(new { items = await queries.List(Transport.GameQuery(c), c.RequestAborted) }))
            .WithMetadata(new Roles("checker"));
        app.MapGet("/checks/{id}", async (string id, HttpContext c, CheckQueries queries) =>
            Results.Json(await queries.Get(id, c.RequestAborted))).WithMetadata(new Roles("checker", "integration"));
        app.MapPost("/checks/next", async (HttpContext c, CheckAssignments handler) =>
        {
            var request = await Transport.Body<GameRequest>(c);
            return Transport.Reply(await handler.Next(await Transport.Identity(c),
                Required.Text(request.GameId), c.RequestAborted));
        }).WithMetadata(new Roles("checker"));
        app.MapPost("/checks/{id}/claim", async (string id, HttpContext c, CheckAssignments handler) =>
            Transport.Reply(await handler.Claim(await Transport.Identity(c), id, c.RequestAborted)))
            .WithMetadata(new Roles("checker"));
        app.MapPost("/checks/{id}/renew", async (string id, HttpContext c, CheckAssignments handler) =>
            Transport.Reply(await handler.Renew(await Transport.Identity(c), id, Transport.Token(c), c.RequestAborted)))
            .WithMetadata(new Roles("checker"));
        app.MapPost("/checks/{id}/release", async (string id, HttpContext c, CheckAssignments handler) =>
            Transport.Reply(await handler.Release(await Transport.Identity(c), id, Transport.Token(c),
                Transport.MayReleaseOtherOwner(c), c.RequestAborted)))
            .WithMetadata(new Roles("checker"));
        app.MapPost("/checks/{id}/verdict", async (string id, HttpContext c, RecordVerdict handler) =>
        {
            var request = await Transport.Body<VerdictRequest>(c);
            return Transport.Reply(await handler.Execute(await Transport.Identity(c), id, Transport.Token(c),
                Verdicts.Parse(request.Verdict), c.RequestAborted));
        }).WithMetadata(new Roles("checker"));
        app.MapPost("/checks/{id}/corrections", async (string id, HttpContext c, CorrectVerdict handler) =>
        {
            var request = await Transport.Body<CorrectionRequest>(c);
            return Transport.Reply(await handler.Execute(await Transport.Identity(c), id,
                Verdicts.Parse(request.Verdict), Required.Text(request.Reason), c.RequestAborted));
        }).WithMetadata(new Roles("admin"));
    }
}
