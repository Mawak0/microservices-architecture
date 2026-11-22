using Olympiad.Http;
using Olympiad.Review.Application;
using Olympiad.Review.Domain;

namespace Olympiad.Review.Api;

internal static class RetakeEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/checks/{id}/self-correction", async (string id, HttpContext c, SelfCorrectVerdict handler) =>
        {
            var body = await Transport.Body<SelfCorrection>(c);
            return Transport.Reply(await handler.Execute(await Transport.Identity(c), id, body.Revision,
                Verdicts.Parse(body.Verdict), c.RequestAborted));
        }).WithMetadata(new Roles("checker"));

        app.MapPost("/checks/{id}/retakes", async (string id, HttpContext c, RetakeActions handler) =>
        {
            var body = await Transport.Body<RetakeRequest>(c);
            return Transport.Reply(await handler.Request(await Transport.Identity(c), id,
                c.Request.Headers["Assignment-Token"].FirstOrDefault(), Transport.MayReleaseOtherOwner(c), body.Revision,
                c.RequestAborted));
        }).WithMetadata(new Roles("checker"));

        app.MapPost("/checks/{id}/retakes/{requestId}/approve", async (string id, string requestId, HttpContext c, RetakeActions handler) =>
        {
            var body = await Transport.Body<Approval>(c);
            return Transport.Reply(await handler.Approve(await Transport.Identity(c), id, requestId,
                Required.Text(body.Reason), c.RequestAborted));
        }).WithMetadata(new Roles("admin"));

        app.MapGet("/retakes", async (HttpContext c, IRetakeWorklist reader) =>
        {
            if (!PrivateResponses.TryPage(c.Request, out var offset, out var limit))
                throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
            var items = await reader.List(Transport.GameQuery(c), offset, limit + 1, c.RequestAborted);
            return Results.Ok(new
            {
                items = items.Take(limit).ToArray(),
                nextOffset = items.Count > limit ? (int?)(offset + limit) : null
            });
        }).WithMetadata(new Roles("observer"));

        app.MapGet("/integrations/retakes/{id}/{requestId}", async (string id, string requestId, HttpContext c, RetakeActions handler) =>
            Results.Ok(await handler.Get(id, requestId, c.RequestAborted))).WithMetadata(new Roles("integration"));

        app.MapPost("/integrations/retake-images", async (HttpContext c, RetakeActions handler) =>
        {
            var message = await Transport.Body<Incoming<RetakeDelivery>>(c);
            if (message.Type != "RetakeImageStored" || message.AggregateId != message.Data.ReceiptId || message.ProcessVersion != 1)
                throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
            return Transport.Reply(await handler.Apply(await Transport.Identity(c), message.Data, c.RequestAborted));
        }).WithMetadata(new Roles("integration"));
    }
    private sealed record SelfCorrection { public required int Revision { get; init; } public required string Verdict { get; init; } }
    private sealed record RetakeRequest { public required int Revision { get; init; } }
    private sealed record Approval { public required string Reason { get; init; } }
}
