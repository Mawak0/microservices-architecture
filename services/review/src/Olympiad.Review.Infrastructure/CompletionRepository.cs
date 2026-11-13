using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Infrastructure;
internal sealed class CompletionRepository(DatabaseScope database) : ICompletionRepository
{
    public IReadOnlyList<string> TrackedGames()
    {
        using var command = database.Command("SELECT game_id FROM completions ORDER BY game_id");
        using var reader = command.ExecuteReader(); var games = new List<string>();
        while (reader.Read()) games.Add(reader.GetString(0));
        return games;
    }
    public GameReviewCompletion Get(string gameId)
    {
        var state = database.Read<CompletionSnapshot>("SELECT snapshot FROM completions WHERE game_id=@game", ("@game", gameId));
        return state is null ? new(gameId) : GameReviewCompletion.Restore(state);
    }
    public void Save(GameReviewCompletion completion)
    {
        var state = completion.Snapshot();
        var snapshot = DatabaseScope.Serialize(state);
        // Polling must remain read-only when the completion state has not changed.
        // Even an UPDATE of the same value creates a new row version and SSI conflicts.
        using var existing = database.Command("SELECT snapshot FROM completions WHERE game_id=@game", ("@game", state.GameId));
        if (existing.ExecuteScalar() is string persisted && persisted == snapshot) return;
        database.Execute("""
            INSERT INTO completions(game_id,snapshot) VALUES(@game,@snapshot)
            ON CONFLICT(game_id) DO UPDATE SET snapshot=excluded.snapshot
            """, ("@game", state.GameId), ("@snapshot", snapshot));
    }
}
