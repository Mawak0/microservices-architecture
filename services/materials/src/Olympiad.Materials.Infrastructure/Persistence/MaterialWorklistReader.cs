using Npgsql;
using Olympiad.Materials.Application;

namespace Olympiad.Materials.Infrastructure.Persistence;

public sealed class MaterialWorklistReader(PostgresDatabase database) : IMaterialWorklistReader
{
    private const string Filters = """
        WHERE game_id=@game AND (CAST(@team AS text) IS NULL OR team_id=@team)
        AND (CAST(@id AS text) IS NULL OR id=@id)
        """;

    public Task<IReadOnlyList<KitListItem>> Kits(MaterialSearch search, CancellationToken ct) => Read(
        "SELECT id,game_id,team_id FROM kits " + Filters + " ORDER BY id OFFSET @offset LIMIT @limit",
        search, r => new KitListItem(r.GetString(0),r.GetString(1),r.GetString(2)), ct);

    public Task<IReadOnlyList<BlankListItem>> Blanks(MaterialSearch search, string? taskId, CancellationToken ct) => Read(
        "SELECT id,game_id,team_id,task_id,task_number,number FROM blanks " + Filters +
        " AND (CAST(@task AS text) IS NULL OR task_id=@task) ORDER BY number OFFSET @offset LIMIT @limit",
        search, r => new BlankListItem(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5)), ct, taskId);

    public Task<IReadOnlyList<PrintListItem>> PrintJobs(MaterialSearch search, CancellationToken ct) => Read(
        "SELECT id,game_id,team_id,status FROM print_jobs " + Filters + " ORDER BY created_order DESC,id OFFSET @offset LIMIT @limit",
        search, r => new PrintListItem(r.GetString(0),r.GetString(1),r.IsDBNull(2) ? null : r.GetString(2),r.GetString(3).ToLowerInvariant()), ct);

    private async Task<IReadOnlyList<T>> Read<T>(string sql, MaterialSearch search, Func<NpgsqlDataReader,T> map,
        CancellationToken ct, string? taskId = null)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("game", search.GameId);
        command.Parameters.AddWithValue("team", NpgsqlTypes.NpgsqlDbType.Text, (object?)search.TeamId ?? DBNull.Value);
        command.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Text, (object?)search.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("task", NpgsqlTypes.NpgsqlDbType.Text, (object?)taskId ?? DBNull.Value);
        command.Parameters.AddWithValue("offset", search.Offset);
        command.Parameters.AddWithValue("limit", search.Limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }
}
