using Olympiad.Http;
using Olympiad.Review.Application;
using Olympiad.Review.Domain;

namespace Olympiad.Review.Api;

internal static class WorklistEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/review-queues", async (HttpContext context, IReviewWorklistReader reader, IClock clock) =>
            Results.Ok(await reader.Counts(Search(context), await clock.Now(context.RequestAborted), context.RequestAborted)))
            .WithMetadata(new Roles("checker"));

        app.MapGet("/checks/activity", async (HttpContext context, IReviewWorklistReader reader, IClock clock) =>
            Results.Ok(await reader.Activity(Search(context), Actor(context).Id,
                await clock.Now(context.RequestAborted), context.RequestAborted)))
            .WithMetadata(new Roles("checker"));

        app.MapGet("/checks/search", async (HttpContext context, IReviewWorklistReader reader, IClock clock) =>
        {
            var search = Search(context);
            var rows = await reader.Search(search, await clock.Now(context.RequestAborted), context.RequestAborted);
            return PrivateResponses.Revalidate(context, Actor(context).Id, new
            {
                items = rows.Take(search.Limit).ToArray(),
                nextOffset = rows.Count > search.Limit ? (int?)(search.Offset + search.Limit) : null
            });
        }).WithMetadata(new Roles("checker", "observer"));
    }

    private static AuthenticatedActor Actor(HttpContext context) => (AuthenticatedActor)context.Items["actor"]!;

    private static ReviewSearch Search(HttpContext context)
    {
        if (!PrivateResponses.TryPage(context.Request, out var offset, out var limit))
            throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
        string? Filter(string key) => context.Request.Query[key].FirstOrDefault();
        return new(Transport.GameQuery(context), Filter("teamId"), Filter("taskId"), Filter("receiptId"),
            Filter("blankId"), Filter("id"), offset, limit);
    }
}
