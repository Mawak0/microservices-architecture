using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;
using Olympiad.Intake.Infrastructure.Persistence;
using Olympiad.Persistence.Postgres;
using Olympiad.Storage.Maintenance;
using Olympiad.Storage.S3;
using Xunit;

namespace Olympiad.Intake.Tests;

[Collection("Offline maintenance")]
public sealed class OrphanCollectionTests
{
    [Fact]
    public async Task CleanupProtectsAcceptedPhotographAndPendingUploadAcrossAdapterRestart()
    {
        var connection=Olympiad.Testing.OfflineDatabase.Create();var database=new PostgresDatabase(connection);await database.InitializeAsync();
        var settings=new S3Options(Environment.GetEnvironmentVariable("TEST_S3_ENDPOINT")!,"us-east-1",Environment.GetEnvironmentVariable("TEST_S3_ACCESS_KEY")!,Environment.GetEnvironmentVariable("TEST_S3_SECRET_KEY")!,"gc-test-"+Guid.NewGuid().ToString("N"));
        using var client=settings.CreateClient();var objects=new S3Objects(client,settings.Bucket);await objects.InitializeAsync(default);
        var accepted=await objects.PutAsync("accepted",new byte[] {1},"image/png",default);
        var pending=await objects.PutAsync("pending",new byte[] {2},"image/png",default);
        var orphan=await objects.PutAsync("orphan",new byte[] {3},"image/png",default);
        var now=DateTimeOffset.UtcNow;
        await database.ExecuteAsync(async(tx,ct)=>
        {
            await tx.Receipts.AddAsync(Receipt.Restore("accepted",new("blank1","game","team","task","1"),1,"/receipts/accepted/image",now),new byte[] {1},"image/png",ct);
            await tx.Submissions.AddAsync(new("pending",new("blank2","game","team","task","1"),new byte[] {2},"image/png",new("scope","key","hash"),"worker",now.AddMinutes(-1)),ct);
            return true;
        },default);
        var collector=new OrphanCollector(new PostgresStore(connection),objects,"intake");
        Assert.Equal(new[] {orphan.Key},await collector.ReconcileAsync(now.AddSeconds(1),true,default));
        Assert.Empty(await new OrphanCollector(new PostgresStore(connection),objects,"intake").ReconcileAsync(now.AddSeconds(1),true,default));
        Assert.Equal(new byte[] {1},(await objects.ReadAsync(accepted,default)).Bytes);
        Assert.Equal(new byte[] {2},(await objects.ReadAsync(pending,default)).Bytes);
        await objects.DeleteAsync(accepted.Key,default);await objects.DeleteAsync(pending.Key,default);
    }
}
[CollectionDefinition("Offline maintenance",DisableParallelization=true)]
public sealed class OfflineMaintenanceCollection;
