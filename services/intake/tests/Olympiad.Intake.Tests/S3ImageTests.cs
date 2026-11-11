using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;
using Olympiad.Intake.Infrastructure.Images;
using Olympiad.Intake.Infrastructure.Persistence;
using Olympiad.Storage.S3;
using Xunit;

namespace Olympiad.Intake.Tests;

public sealed class S3ImageTests
{
    [Fact]
    public async Task AcceptedImageLivesInPrivateS3AndIsReadThroughCommittedMetadataAfterRestart()
    {
        var options = new S3Options(Environment.GetEnvironmentVariable("TEST_S3_ENDPOINT") ?? throw new InvalidOperationException("Set TEST_S3_ENDPOINT"),
            "us-east-1", Environment.GetEnvironmentVariable("TEST_S3_ACCESS_KEY")!, Environment.GetEnvironmentVariable("TEST_S3_SECRET_KEY")!, "answer-images");
        using var client = options.CreateClient(); var objects = new S3Objects(client, options.Bucket); await objects.InitializeAsync(default);
        var images = new S3ImageStorage(objects); var id = Guid.NewGuid().ToString(); byte[] bytes = [1, 2, 3, 4];
        var reference = await images.SaveAsync(id, "blank", bytes, "image/png", default);
        Assert.Equal(reference, await images.SaveAsync(id, "blank", bytes, "image/png", default));
        var connectionString = TestDatabase.For(id); var database = new PostgresDatabase(connectionString); await database.InitializeAsync();
        var receipt = Receipt.Restore(id, new("blank", "game", "team", "task", "1"), 1, reference, DateTimeOffset.UtcNow);
        await database.ExecuteAsync(async (tx, ct) => { await tx.Receipts.AddAsync(receipt, bytes, "image/png", ct); return true; }, default);
        using var restartedClient = options.CreateClient();
        var reader = new GetImage(new PostgresDatabase(connectionString), new S3ImageStorage(new S3Objects(restartedClient, options.Bucket)));
        var restored = await reader.ExecuteAsync(id, default);
        Assert.Equal(bytes, restored.Bytes); Assert.Equal("image/png", restored.ContentType);
        var missing = await Assert.ThrowsAsync<IntakeException>(() => reader.ExecuteAsync("missing", default));
        Assert.Equal(IntakeError.NotFound, missing.Error);
        await using var sql = new Npgsql.NpgsqlConnection(connectionString); await sql.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='receipts' AND data_type='bytea'", sql);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
