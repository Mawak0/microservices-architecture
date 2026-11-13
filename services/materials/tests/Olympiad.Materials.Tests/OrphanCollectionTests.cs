using Olympiad.Materials.Domain;
using Olympiad.Materials.Infrastructure.Persistence;
using Olympiad.Persistence.Postgres;
using Olympiad.Storage.Maintenance;
using Olympiad.Storage.S3;
using Xunit;

namespace Olympiad.Materials.Tests;

[Collection("HTTP environment")]
public sealed class OrphanCollectionTests
{
    [Fact]
    public async Task OfflineCleanupKeepsCommittedAndRecoverablePdfAndDeletesOnlyOrphans()
    {
        var connection=Olympiad.Testing.OfflineDatabase.Create();var database=new PostgresDatabase(connection);await database.InitializeAsync();
        var settings=new S3Options(Environment.GetEnvironmentVariable("TEST_S3_ENDPOINT")!,"us-east-1",Environment.GetEnvironmentVariable("TEST_S3_ACCESS_KEY")!,Environment.GetEnvironmentVariable("TEST_S3_SECRET_KEY")!,"gc-test-"+Guid.NewGuid().ToString("N"));
        using var client=settings.CreateClient();var objects=new S3Objects(client,settings.Bucket);await objects.InitializeAsync(default);
        var valid=await objects.PutAsync("ready:worker",new byte[] {1},"application/pdf",default);
        var recoverable=await objects.PutAsync("running:worker",new byte[] {2},"application/pdf",default);
        var orphan=await objects.PutAsync("abandoned:worker",new byte[] {3},"application/pdf",default);
        var transactions=new PostgresMaterialsTransactions(database);
        await transactions.ExecuteAsync(async(s,ct)=>
        {
            await s.PrintJobs.AddAsync(new("ready","game","team",Sample.Snapshot(),PrintStatus.Completed,"/pdf"),ct);
            await s.PrintJobs.StoreArtifactAsync("ready",new(valid.Key,valid.ContentType,valid.Size,valid.Sha256),ct);
            await s.PrintJobs.AddAsync(new("running","game","team",Sample.Snapshot(),PrintStatus.Running,processingToken:"worker",processingExpiresAt:DateTimeOffset.UtcNow.AddMinutes(-1)),ct);
            return true;
        },default);
        var collector=new OrphanCollector(new PostgresStore(connection),objects,"materials");
        Assert.Empty(await collector.ReconcileAsync(DateTimeOffset.UtcNow.AddDays(-1),false,default));
        Assert.Equal(new[] {orphan.Key},await collector.ReconcileAsync(DateTimeOffset.UtcNow.AddSeconds(1),false,default));
        Assert.Equal(new byte[] {3},(await objects.ReadAsync(orphan,default)).Bytes);
        await using(var active=new Npgsql.NpgsqlConnection(connection))
        {
            await active.OpenAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(()=>collector.ReconcileAsync(DateTimeOffset.UtcNow.AddSeconds(1),true,default));
        }
        Assert.Equal(new[] {orphan.Key},await collector.ReconcileAsync(DateTimeOffset.UtcNow.AddSeconds(1),true,default));
        Assert.Empty(await collector.ReconcileAsync(DateTimeOffset.UtcNow.AddSeconds(1),true,default));
        Assert.Equal(new byte[] {1},(await objects.ReadAsync(valid,default)).Bytes);
        Assert.Equal(new byte[] {2},(await objects.ReadAsync(recoverable,default)).Bytes);
        await objects.DeleteAsync(valid.Key,default);await objects.DeleteAsync(recoverable.Key,default);
    }
}
