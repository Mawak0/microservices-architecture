using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Olympiad.Storage.S3;
using Xunit;

namespace Olympiad.Materials.Tests;

public sealed class S3ObjectTests
{
    private static S3Options Options => new(
        Environment.GetEnvironmentVariable("TEST_S3_ENDPOINT") ?? throw new InvalidOperationException("Set TEST_S3_ENDPOINT"),
        "us-east-1", Environment.GetEnvironmentVariable("TEST_S3_ACCESS_KEY") ?? throw new InvalidOperationException("Set TEST_S3_ACCESS_KEY"),
        Environment.GetEnvironmentVariable("TEST_S3_SECRET_KEY") ?? throw new InvalidOperationException("Set TEST_S3_SECRET_KEY"), "print-materials");

    [Fact]
    public async Task RestartAndDuplicateUploadRetainOnePrivateVerifiedObject()
    {
        using var client = Options.CreateClient(); var store = new S3Objects(client, Options.Bucket);
        await store.InitializeAsync(default); await store.InitializeAsync(default);
        var owner = Guid.NewGuid().ToString(); byte[] bytes = [1, 2, 3, 4];
        var first = await store.PutAsync(owner, bytes, "application/pdf", default);
        using var restartedClient = Options.CreateClient(); var restarted = new S3Objects(restartedClient, Options.Bucket);
        Assert.Equal(first, await restarted.PutAsync(owner, bytes, "application/pdf", default));
        Assert.Equal(bytes, (await restarted.ReadAsync(first, default)).Bytes);
        Assert.NotEqual(first.Key, (await store.PutAsync(owner, new byte[] { 8, 9 }, "application/pdf", default)).Key);
        Assert.Equal(bytes, (await restarted.ReadAsync(first, default)).Bytes);
        using var anonymous = new HttpClient();
        using var denied = await anonymous.GetAsync(Options.Endpoint + "/" + Options.Bucket + "/" + first.Key);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
    [Fact]
    public async Task LostUploadAcknowledgementCanBeRetriedWithoutChangingCommittedBytes()
    {
        using var client = Options.CreateClient(); var store = new S3Objects(client, Options.Bucket); await store.InitializeAsync(default);
        using var unreliable = new LoseAcknowledgement(client); var sender = new S3Objects(unreliable, Options.Bucket);
        var owner=Guid.NewGuid().ToString(); byte[] bytes = [4, 5, 6];
        await Assert.ThrowsAsync<AmazonS3Exception>(() => sender.PutAsync(owner, bytes, "application/pdf", default));
        var restored = await sender.PutAsync(owner, bytes, "application/pdf", default);
        Assert.Equal(bytes, (await store.ReadAsync(restored, default)).Bytes);
        var objects = await client.ListObjectsV2Async(new() { BucketName=Options.Bucket, Prefix=restored.Key.Split('/')[0] });
        Assert.Single(objects.S3Objects);
    }
    [Fact]
    public async Task MetadataAndChecksumMismatchAreRejectedAndDeletionIsExplicit()
    {
        using var client = Options.CreateClient(); var store = new S3Objects(client, Options.Bucket); await store.InitializeAsync(default);
        var stored = await store.PutAsync(Guid.NewGuid().ToString(), new byte[] { 7, 8 }, "application/pdf", default);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(stored with { Size=99 }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(stored with { ContentType="image/png" }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(stored with { Sha256="incorrect" }, default));
        Assert.Contains(stored.Key, await store.ListOlderThanAsync(DateTimeOffset.UtcNow.AddHours(1), default));
        Assert.DoesNotContain(stored.Key, await store.ListOlderThanAsync(DateTimeOffset.UnixEpoch, default));
        await store.DeleteAsync(stored.Key, default);
        await Assert.ThrowsAsync<AmazonS3Exception>(() => store.ReadAsync(stored, default));
    }
    [Fact]
    public async Task InvalidInputAndCancellationNeverCreateAnObject()
    {
        using var client = Options.CreateClient(); var store = new S3Objects(client, Options.Bucket);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync("", new byte[] { 1 }, "application/pdf", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync("owner", ReadOnlyMemory<byte>.Empty, "application/pdf", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync("owner", new byte[] { 1 }, "application/executable", default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutAsync("owner", new byte[] { 1 }, "application/pdf", cancelled.Token));
        Assert.Throws<ArgumentException>(() => (Options with { Endpoint="invalid" }).CreateClient());
        Assert.Throws<ArgumentException>(() => (Options with { SecretKey="" }).CreateClient());
    }
    private sealed class LoseAcknowledgement(IAmazonS3 actual) : AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL=Options.Endpoint })
    {
        private bool first = true;
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken ct = default)
        {
            var response = await actual.PutObjectAsync(request, ct);
            if (first) { first=false; throw new AmazonS3Exception("Lost acknowledgement") { StatusCode=HttpStatusCode.ServiceUnavailable }; }
            return response;
        }
        public override Task<GetObjectResponse> GetObjectAsync(string bucket, string key, CancellationToken ct = default) => actual.GetObjectAsync(bucket,key,ct);
    }
}
