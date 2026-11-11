using System.Security.Cryptography;
using System.Text;
using Npgsql;
namespace Olympiad.Review.Tests;
internal static class TestDatabase
{
    public static string For(string identity)
    {
        var configured = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("Set TEST_POSTGRES_CONNECTION to a dedicated PostgreSQL test database.");
        var schema = "test_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
        using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(configured) { Pooling = false }.ConnectionString); connection.Open();
        using var command = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS " + schema, connection); command.ExecuteNonQuery();
        return new NpgsqlConnectionStringBuilder(configured) { SearchPath = schema, Pooling = false }.ConnectionString;
    }
}
