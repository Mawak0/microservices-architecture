using Microsoft.Data.Sqlite;
using Sapientum.Materials.Application;
using Sapientum.Materials.Domain;
using System.Globalization;

namespace Sapientum.Materials.Infrastructure.Persistence;

internal sealed class SqliteBlankRepository(SqliteConnection connection, SqliteTransaction transaction)
    : SqliteRepository(connection, transaction), IBlankRepository
{
    public async Task<long> ReserveNumbersAsync(int count, CancellationToken cancellationToken)
    {
        await using var command = Command("UPDATE blank_sequence SET value=value+$count WHERE id=1 RETURNING value", ("$count", count));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) - count + 1;
    }
    public async Task AddAsync(AnswerBlank blank, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            INSERT INTO blanks VALUES($id,$number,$game,$team,$task,$taskNumber,$qr,$modifierKind,$modifierValue)
            """, ("$id", blank.Id), ("$number", blank.Number), ("$game", blank.GameId), ("$team", blank.TeamId),
            ("$task", blank.TaskId), ("$taskNumber", blank.TaskNumber), ("$qr", blank.QrToken),
            ("$modifierKind", blank.Modifier?.Kind.ToString()), ("$modifierValue", blank.Modifier?.Value.ToString(CultureInfo.InvariantCulture)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public Task<AnswerBlank?> FindAsync(string id, CancellationToken cancellationToken) => FindByAsync("id", id, cancellationToken);
    public Task<AnswerBlank?> FindByTokenAsync(string token, CancellationToken cancellationToken) => FindByAsync("qr_token", token, cancellationToken);
    private async Task<AnswerBlank?> FindByAsync(string column, string value, CancellationToken cancellationToken)
    {
        await using var command = Command($"SELECT id,number,game_id,team_id,task_id,task_number,qr_token,modifier_kind,modifier_value FROM blanks WHERE {column}=$value", ("$value", value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var modifier = reader.IsDBNull(7) ? null : new ScoreModifier(Enum.Parse<ModifierKind>(reader.GetString(7)), decimal.Parse(reader.GetString(8), CultureInfo.InvariantCulture));
        return new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), modifier);
    }
}
