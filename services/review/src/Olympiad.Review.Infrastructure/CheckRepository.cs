using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Infrastructure;
internal sealed class CheckRepository(DatabaseScope database) : ICheckRepository
{
    public AnswerCheck? Find(string id) => Restore(database.Read<CheckSnapshot>(
        "SELECT snapshot FROM checks WHERE id=@id", ("@id", id)));
    public AnswerCheck? FindByReceipt(string receiptId) => Restore(database.Read<CheckSnapshot>(
        "SELECT snapshot FROM checks WHERE receipt_id=@id", ("@id", receiptId)));
    public IReadOnlyList<AnswerCheck> ForGame(string gameId) => database.ReadMany<CheckSnapshot>(
        "SELECT snapshot FROM checks WHERE game_id=@game ORDER BY arrival_order", ("@game", gameId))
        .Select(AnswerCheck.Restore).ToArray();
    public AnswerCheck? NextAvailable(string gameId, DateTimeOffset now, bool appeal) => Restore(
        database.Read<CheckSnapshot>("""
            SELECT snapshot FROM checks WHERE game_id=@game AND
                (queue_stage=@pending OR (queue_stage=@working AND assignment_until<=@now))
            ORDER BY arrival_order LIMIT 1 FOR UPDATE SKIP LOCKED
            """, ("@game", gameId), ("@now", now.UtcDateTime),
            ("@pending", (int)(appeal ? CheckStage.AppealPending : CheckStage.Pending)),
            ("@working", (int)(appeal ? CheckStage.AppealChecking : CheckStage.Checking))));

    public int CountAssigned(string actorId, DateTimeOffset now)
    {
        using var command = database.Command("""
            SELECT count(*) FROM checks WHERE assigned_actor=@actor AND assignment_until>@now
            """, ("@actor", actorId), ("@now", now.UtcDateTime));
        return checked((int)(long)command.ExecuteScalar()!);
    }

    public void Save(AnswerCheck check)
    {
        var snapshot = check.Snapshot();
        var last = snapshot.History.LastOrDefault();
        database.Execute("""
            INSERT INTO checks(id,receipt_id,game_id,snapshot,queue_stage,assigned_actor,assignment_until,
                last_verdict_actor,last_verdict_at,subject_team,subject_task,subject_blank)
            VALUES(@id,@receipt,@game,@snapshot,@stage,@actor,CAST(@until AS timestamptz),
                @last_actor,CAST(@last_at AS timestamptz),@team,@task,@blank)
            ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot,queue_stage=excluded.queue_stage,
                assigned_actor=excluded.assigned_actor,assignment_until=excluded.assignment_until,
                last_verdict_actor=excluded.last_verdict_actor,last_verdict_at=excluded.last_verdict_at
            """, ("@id", check.Id), ("@receipt", check.Receipt.Id), ("@game", check.Receipt.GameId),
            ("@snapshot", DatabaseScope.Serialize(snapshot)), ("@stage", (int)snapshot.Stage),
            ("@actor", snapshot.Assignment?.ActorId), ("@until", snapshot.Assignment?.ExpiresAt.ToString("O")),
            ("@last_actor", last?.ActorId), ("@last_at", last?.RecordedAt.ToString("O")),
            ("@team", check.Receipt.TeamId), ("@task", check.Receipt.TaskId), ("@blank", check.Receipt.BlankId));
    }
    private static AnswerCheck? Restore(CheckSnapshot? snapshot) => snapshot is null ? null : AnswerCheck.Restore(snapshot);
}
