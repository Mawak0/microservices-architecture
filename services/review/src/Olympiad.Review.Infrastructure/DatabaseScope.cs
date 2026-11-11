using System.Text.Json;
using Npgsql;
namespace Olympiad.Review.Infrastructure;
internal sealed class DatabaseScope(NpgsqlConnection connection, NpgsqlTransaction transaction,
    CancellationToken cancellationToken)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public NpgsqlCommand Command(string sql, params (string Name, object? Value)[] values)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public T? Read<T>(string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, values); var raw = command.ExecuteScalar();
        return raw is string json ? JsonSerializer.Deserialize<T>(json, Json) : default;
    }
    public IReadOnlyList<T> ReadMany<T>(string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, values); using var reader = command.ExecuteReader();
        var valuesList = new List<T>();
        while (reader.Read()) valuesList.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return valuesList;
    }
    public void Execute(string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, values); command.ExecuteNonQuery();
    }
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
