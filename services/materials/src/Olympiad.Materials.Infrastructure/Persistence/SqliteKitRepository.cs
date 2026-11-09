using Microsoft.Data.Sqlite;
using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Infrastructure.Persistence;

internal sealed class SqliteKitRepository(SqliteConnection connection, SqliteTransaction transaction)
    : SqliteRepository(connection, transaction), IKitRepository
{
    public Task<TeamKit?> FindAsync(string id, CancellationToken cancellationToken) => FindByAsync("id=$id", [("$id", id)], cancellationToken);
    public Task<TeamKit?> FindForTeamAsync(string gameId, string teamId, CancellationToken cancellationToken) =>
        FindByAsync("game_id=$game AND team_id=$team", [("$game", gameId), ("$team", teamId)], cancellationToken);
    private async Task<TeamKit?> FindByAsync(string where, (string, object?)[] parameters, CancellationToken cancellationToken)
    {
        string id, gameId, teamId, template; int blanks, cards; bool shared;
        await using (var command = Command($"SELECT id,game_id,team_id,answer_blank_count,task_card_count,task_cards_shared,template_key FROM kits WHERE {where}", parameters))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            id = reader.GetString(0); gameId = reader.GetString(1); teamId = reader.GetString(2); blanks = reader.GetInt32(3);
            cards = reader.GetInt32(4); shared = reader.GetBoolean(5); template = reader.GetString(6);
        }
        var ids = new List<string>();
        await using var selected = Command("SELECT blank_id FROM kit_blanks WHERE kit_id=$id ORDER BY ordinal", ("$id", id));
        await using var selectedReader = await selected.ExecuteReaderAsync(cancellationToken);
        while (await selectedReader.ReadAsync(cancellationToken)) ids.Add(selectedReader.GetString(0));
        return new(id, gameId, teamId, blanks, cards, shared, template, ids);
    }
    public async Task AddAsync(TeamKit kit, CancellationToken cancellationToken)
    {
        await using var command = Command("INSERT INTO kits VALUES($id,$game,$team,$blanks,$cards,$shared,$template)",
            ("$id", kit.Id), ("$game", kit.GameId), ("$team", kit.TeamId), ("$blanks", kit.AnswerBlankCount),
            ("$cards", kit.TaskCardCount), ("$shared", kit.TaskCardsShared), ("$template", kit.TemplateKey));
        await command.ExecuteNonQueryAsync(cancellationToken);
        for (var i = 0; i < kit.BlankIds.Count; i++)
        {
            await using var linked = Command("INSERT INTO kit_blanks VALUES($kit,$blank,$ordinal)", ("$kit", kit.Id), ("$blank", kit.BlankIds[i]), ("$ordinal", i));
            await linked.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
