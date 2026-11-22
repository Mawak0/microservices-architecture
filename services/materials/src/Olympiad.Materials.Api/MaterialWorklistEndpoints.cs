using Olympiad.Http;
using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Api;

internal static class MaterialWorklistEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/kits", (HttpContext c, IMaterialWorklistReader reader) =>
            Page(c, MaterialPermission.ReadKit, reader.Kits));
        app.MapGet("/blanks", (HttpContext c, IMaterialWorklistReader reader) =>
            Page(c, MaterialPermission.Read, (search, ct) => reader.Blanks(search, c.Request.Query["taskId"].FirstOrDefault(), ct)));
        app.MapGet("/print-jobs", (HttpContext c, IMaterialWorklistReader reader) =>
            Page(c, MaterialPermission.Print, reader.PrintJobs));
    }

    private static async Task<IResult> Page<T>(HttpContext context, MaterialPermission permission,
        Func<MaterialSearch,CancellationToken,Task<IReadOnlyList<T>>> query)
    {
        var actor = context.Actor();
        actor.Require(permission);
        if (!PrivateResponses.TryPage(context.Request, out var offset, out var limit))
            throw new DomainException("VALIDATION_ERROR", "Invalid pagination.");
        var keys = context.Request.Query;
        var search = new MaterialSearch(Transport.Required(keys["gameId"].FirstOrDefault()),
            keys["teamId"].FirstOrDefault(), keys["id"].FirstOrDefault(), offset, limit);
        var page = await query(search, context.RequestAborted);
        return PrivateResponses.Revalidate(context, actor.Id, new
        {
            items = page.Take(limit).ToArray(),
            nextOffset = page.Count > limit ? (int?)(offset + limit) : null
        });
    }
}
