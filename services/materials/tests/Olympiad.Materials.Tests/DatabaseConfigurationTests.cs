using Npgsql;
using Olympiad.Materials.Infrastructure.Persistence;
using Xunit;

namespace Olympiad.Materials.Tests;

public sealed class DatabaseConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitPoolingSettingIsPreserved(bool pooling)
    {
        var database = new PostgresDatabase(new NpgsqlConnectionStringBuilder(TestDatabase.For(Guid.NewGuid().ToString())) { Pooling = pooling }.ConnectionString);
        await using var connection = await database.OpenAsync(default);
        Assert.Equal(pooling, new NpgsqlConnectionStringBuilder(connection.ConnectionString).Pooling);
    }
}
