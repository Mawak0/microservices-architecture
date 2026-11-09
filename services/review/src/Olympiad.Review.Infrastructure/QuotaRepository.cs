using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Infrastructure;
internal sealed class QuotaRepository(DatabaseScope database) : IQuotaRepository
{
    public GameAppealQuota Get(string gameId)
    {
        using var command = database.Command("SELECT limit_value,used FROM quotas WHERE game_id=$game", ("$game", gameId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(gameId, reader.GetInt32(0), reader.GetInt32(1)) : new(gameId, 0);
    }
    public void Save(GameAppealQuota quota) => database.Execute("""
        INSERT INTO quotas(game_id,limit_value,used) VALUES($game,$limit,$used)
        ON CONFLICT(game_id) DO UPDATE SET limit_value=excluded.limit_value,used=excluded.used
        """, ("$game", quota.GameId), ("$limit", quota.Limit), ("$used", quota.Used));
}
