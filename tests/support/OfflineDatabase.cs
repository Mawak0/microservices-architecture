using Npgsql;

namespace Olympiad.Testing;

internal static class OfflineDatabase
{
    public static string Create()
    {
        var settings=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ??throw new InvalidOperationException("Set TEST_POSTGRES_CONNECTION")) {Pooling=false};
        var name="offline_gc_"+Guid.NewGuid().ToString("N");
        using(var connection=new NpgsqlConnection(settings.ConnectionString))
        {
            connection.Open();using var command=new NpgsqlCommand("CREATE DATABASE "+name,connection);command.ExecuteNonQuery();
        }
        settings.Database=name;settings.SearchPath="public";
        return settings.ConnectionString;
    }
}
