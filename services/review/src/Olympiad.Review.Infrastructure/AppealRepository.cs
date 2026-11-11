using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Infrastructure;
internal sealed class AppealRepository(DatabaseScope database) : IAppealRepository
{
    public Appeal? Find(string id)
    {
        var state = database.Read<AppealSnapshot>("SELECT snapshot FROM appeals WHERE id=@id", ("@id", id));
        return state is null ? null : Appeal.Restore(state);
    }
    public bool Exists(string gameId, string teamId, string taskId)
    {
        using var command = database.Command("""
            SELECT count(*) FROM appeals WHERE game_id=@game AND team_id=@team AND task_id=@task
            """, ("@game", gameId), ("@team", teamId), ("@task", taskId));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }
    public IReadOnlyList<Appeal> ForGame(string gameId) => database.ReadMany<AppealSnapshot>(
        "SELECT snapshot FROM appeals WHERE game_id=@game ORDER BY arrival_order", ("@game", gameId))
        .Select(Appeal.Restore).ToArray();
    public void Save(Appeal appeal) => database.Execute("""
        INSERT INTO appeals(id,check_id,game_id,team_id,task_id,snapshot)
        VALUES(@id,@check,@game,@team,@task,@snapshot)
        ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot
        """, ("@id", appeal.Id), ("@check", appeal.CheckId), ("@game", appeal.GameId),
        ("@team", appeal.TeamId), ("@task", appeal.TaskId),
        ("@snapshot", DatabaseScope.Serialize(appeal.Snapshot())));
}
