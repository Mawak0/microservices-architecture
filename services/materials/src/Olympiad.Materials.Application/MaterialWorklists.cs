namespace Olympiad.Materials.Application;

public sealed record MaterialSearch(string GameId, string? TeamId, string? Id, int Offset, int Limit);
public sealed record KitListItem(string Id, string GameId, string TeamId);
public sealed record BlankListItem(string Id, string GameId, string TeamId, string TaskId, string TaskNumber, long Number);
public sealed record PrintListItem(string Id, string GameId, string? TeamId, string Status);
public interface IMaterialWorklistReader
{
    Task<IReadOnlyList<KitListItem>> Kits(MaterialSearch search, CancellationToken ct);
    Task<IReadOnlyList<BlankListItem>> Blanks(MaterialSearch search, string? taskId, CancellationToken ct);
    Task<IReadOnlyList<PrintListItem>> PrintJobs(MaterialSearch search, CancellationToken ct);
}
