using Olympiad.Review.Application;
using Olympiad.Review.Domain;

namespace Olympiad.Review.Infrastructure;

public sealed class ReviewWorklistReader(PostgresDatabase database) : IReviewWorklistReader
{
    public Task<QueueCounts> Counts(ReviewSearch search, DateTimeOffset now, CancellationToken ct) =>
        Read(db =>
        {
            using var command = db.Command("""
                SELECT count(*) FILTER (WHERE queue_stage=0 OR (queue_stage=1 AND assignment_until<=@now)),
                       count(*) FILTER (WHERE queue_stage=3 OR (queue_stage=4 AND assignment_until<=@now))
                FROM checks WHERE game_id=@game AND queue_stage IN (0,1,3,4)
                """, ("@game", search.GameId), ("@now", now.UtcDateTime));
            using var reader = command.ExecuteReader();
            reader.Read();
            return new QueueCounts(checked((int)reader.GetInt64(0)), checked((int)reader.GetInt64(1)));
        }, ct);

    public Task<IReadOnlyList<CheckSummary>> Search(ReviewSearch search, DateTimeOffset now, CancellationToken ct) =>
        Read<IReadOnlyList<CheckSummary>>(db => db.ReadMany<CheckSnapshot>("""
            SELECT snapshot FROM checks WHERE game_id=@game
              AND (CAST(@team AS text) IS NULL OR subject_team=@team)
              AND (CAST(@task AS text) IS NULL OR subject_task=@task)
              AND (CAST(@receipt AS text) IS NULL OR receipt_id=@receipt)
              AND (CAST(@blank AS text) IS NULL OR subject_blank=@blank)
              AND (CAST(@id AS text) IS NULL OR id=@id)
            ORDER BY arrival_order DESC OFFSET @offset LIMIT @limit
            """, ("@game", search.GameId), ("@team", search.TeamId), ("@task", search.TaskId),
            ("@receipt", search.ReceiptId), ("@blank", search.BlankId), ("@id", search.Id),
            ("@offset", search.Offset), ("@limit", search.Limit + 1)).Select(c => Summary(c, now)).ToArray(), ct);

    public Task<ReviewerActivity> Activity(ReviewSearch search, string actorId, DateTimeOffset now, CancellationToken ct) =>
        Read(db =>
        {
            var assignments = db.ReadMany<CheckSnapshot>("""
                SELECT snapshot FROM checks WHERE game_id=@game AND assigned_actor=@actor AND assignment_until>@now
                ORDER BY arrival_order
                """, ("@game", search.GameId), ("@actor", actorId), ("@now", now.UtcDateTime));
            var decisions = db.ReadMany<CheckSnapshot>("""
                SELECT snapshot FROM checks WHERE game_id=@game AND last_verdict_actor=@actor
                ORDER BY last_verdict_at DESC,arrival_order DESC OFFSET @offset LIMIT @limit
                """, ("@game", search.GameId), ("@actor", actorId), ("@offset", search.Offset), ("@limit", search.Limit + 1));
            return new ReviewerActivity(assignments.Select(c => CheckView.From(AnswerCheck.Restore(c))).ToArray(),
                decisions.Take(search.Limit).Select(c => Summary(c, now)).ToArray(),
                decisions.Count > search.Limit ? search.Offset + search.Limit : null);
        }, ct);

    private Task<T> Read<T>(Func<DatabaseScope, T> query, CancellationToken ct) =>
        Task.FromResult(database.Store.Run((connection, transaction) => query(new(connection, transaction, ct)), ct));

    private static CheckSummary Summary(CheckSnapshot snapshot, DateTimeOffset now)
    {
        var check = AnswerCheck.Restore(snapshot);
        check.Expire(now);
        var view = CheckView.From(check);
        return new(view.Id, view.ReceiptId, view.GameId, view.TeamId, view.TaskId, view.TaskNumber,
            view.Status, view.Verdict, view.Revision);
    }
}
