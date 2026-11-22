namespace Olympiad.Review.Application;

public sealed record QueueCounts(int Answers, int Appeals);
public sealed record ReviewSearch(string GameId, string? TeamId, string? TaskId, string? ReceiptId,
    string? BlankId, string? Id, int Offset, int Limit);
public sealed record CheckSummary(string Id, string ReceiptId, string GameId, string TeamId, string TaskId,
    string TaskNumber, string Status, string? Verdict, int Revision);
public sealed record ReviewerActivity(IReadOnlyList<CheckView> Assignments, IReadOnlyList<CheckSummary> Decisions, int? NextOffset);

public interface IReviewWorklistReader
{
    Task<QueueCounts> Counts(ReviewSearch search, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<CheckSummary>> Search(ReviewSearch search, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ReviewerActivity> Activity(ReviewSearch search, string actorId, DateTimeOffset now, CancellationToken cancellationToken);
}
