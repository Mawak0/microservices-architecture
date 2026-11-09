using Microsoft.Data.Sqlite;
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
        var database = new SqliteDatabase($"Data Source=:memory:;Pooling={pooling}");
        await using var connection = await database.OpenAsync(default);
        Assert.Equal(pooling, new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling);
    }
}
