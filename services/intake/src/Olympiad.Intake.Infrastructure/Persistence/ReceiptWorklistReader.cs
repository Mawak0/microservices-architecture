using Npgsql;
using Olympiad.Intake.Application;

namespace Olympiad.Intake.Infrastructure.Persistence;

public sealed class ReceiptWorklistReader(PostgresDatabase database) : IReceiptWorklistReader
{
    public async Task<IReadOnlyList<ReceiptListItem>> SearchAsync(ReceiptSearch search, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var c = new NpgsqlCommand("""
            SELECT id,blank_id,game_id,team_id,task_id,task_number,accepted_at FROM receipts
            WHERE game_id=@game AND (CAST(@team AS text) IS NULL OR team_id=@team)
                AND (CAST(@task AS text) IS NULL OR task_id=@task)
                AND (CAST(@blank AS text) IS NULL OR blank_id=@blank)
                AND (CAST(@id AS text) IS NULL OR id=@id)
            ORDER BY accepted_at DESC,id OFFSET @offset LIMIT @limit
            """, connection);
        c.Parameters.AddWithValue("game", search.GameId);
        c.Parameters.AddWithValue("team", NpgsqlTypes.NpgsqlDbType.Text, (object?)search.TeamId ?? DBNull.Value);
        c.Parameters.AddWithValue("task", NpgsqlTypes.NpgsqlDbType.Text, (object?)search.TaskId ?? DBNull.Value);
        c.Parameters.AddWithValue("blank", NpgsqlTypes.NpgsqlDbType.Text, (object?)search.BlankId ?? DBNull.Value);
        c.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Text, (object?)search.Id ?? DBNull.Value);
        c.Parameters.AddWithValue("offset", search.Offset);
        c.Parameters.AddWithValue("limit", search.Limit + 1);
        await using var reader = await c.ExecuteReaderAsync(ct);
        var rows = new List<ReceiptListItem>();
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),
                reader.GetString(5),DateTimeOffset.Parse(reader.GetString(6))));
        return rows;
    }
}
