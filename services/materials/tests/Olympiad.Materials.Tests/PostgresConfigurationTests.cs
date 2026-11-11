using Olympiad.Persistence.Postgres;
using Xunit;

namespace Olympiad.Materials.Tests;

public sealed class PostgresConfigurationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Data Source=previous.db")]
    [InlineData("Host=127.0.0.1")]
    [InlineData("Database=materials")]
    public void MissingOrNonPostgresConfigurationFailsClosed(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => new PostgresStore(value));

    [Fact]
    public async Task CancelledTransactionDoesNotStartBusinessWork()
    {
        var store = new PostgresStore(TestDatabase.For(Guid.NewGuid().ToString()));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RunAsync<int>((_, _, _) =>
            throw new InvalidOperationException("Must not start a cancelled request"), cancellation.Token));
    }
}
