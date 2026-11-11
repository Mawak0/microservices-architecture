using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Olympiad.Storage.S3;

public sealed record ObjectMetadata(string Key, string ContentType, long Size, string Sha256);
public sealed record ObjectContent(ObjectMetadata Metadata, byte[] Bytes);
public sealed record S3Options(string Endpoint, string Region, string AccessKey, string SecretKey, string Bucket)
{
    public IAmazonS3 CreateClient()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http") ||
            string.IsNullOrWhiteSpace(Region) || string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey) ||
            string.IsNullOrWhiteSpace(Bucket)) throw new ArgumentException("Configure S3 endpoint, region, credentials and private bucket.");
        return new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config
        { ServiceURL = address.AbsoluteUri, AuthenticationRegion = Region, ForcePathStyle = true, Timeout = TimeSpan.FromSeconds(30) });
    }
}

/// <summary>Private immutable, content-addressed objects; callers persist metadata in their own transaction.</summary>
public sealed class S3Objects(IAmazonS3 client, string bucket)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        try { await client.GetBucketLocationAsync(bucket, ct); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            try { await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = false }, ct); }
            catch (AmazonS3Exception conflict) when (conflict.ErrorCode == "BucketAlreadyOwnedByYou") { }
        }
    }
    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Key(string owner, string digest) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(owner))).ToLowerInvariant() + "/" + digest;

    public async Task<ObjectMetadata> PutAsync(string owner, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner) || bytes.Length == 0 || bytes.Length > 100 * 1024 * 1024 ||
            contentType is not ("image/png" or "image/jpeg" or "application/pdf" or "application/json"))
            throw new ArgumentException("A bounded supported object and owner are required.");
        var digest = Digest(bytes.Span);
        var metadata = new ObjectMetadata(Key(owner, digest), contentType, bytes.Length, digest);
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        var request = new PutObjectRequest
        {
            BucketName = bucket, Key = metadata.Key, InputStream = stream, ContentType = contentType,
            AutoCloseStream = false, IfNoneMatch = "*"
        };
        request.Metadata.Add("sha256", digest);
        try { await client.PutObjectAsync(request, ct); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // A lost upload acknowledgement must not create a second object or overwrite the first.
            await ReadAsync(metadata, ct);
        }
        return metadata;
    }
    public async Task<ObjectContent> ReadAsync(ObjectMetadata expected, CancellationToken ct)
    {
        using var response = await client.GetObjectAsync(bucket, expected.Key, ct);
        if (response.ContentLength != expected.Size || response.Headers.ContentType != expected.ContentType)
            throw new InvalidDataException("Stored object metadata differs from the committed reference.");
        using var buffer = new MemoryStream(); await response.ResponseStream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        if (bytes.LongLength != expected.Size || Digest(bytes) != expected.Sha256)
            throw new InvalidDataException("Stored object checksum differs from the committed reference.");
        return new(expected, bytes);
    }
    public Task DeleteAsync(string key, CancellationToken ct) => client.DeleteObjectAsync(bucket, key, ct);
    public async Task<IReadOnlyList<string>> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var result = new List<string>(); string? continuation = null;
        do
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, ContinuationToken = continuation }, ct);
            result.AddRange(page.S3Objects.Where(o => o.LastModified.ToUniversalTime() < cutoff.UtcDateTime).Select(o => o.Key));
            continuation = page.IsTruncated ? page.NextContinuationToken : null;
        } while (continuation is not null);
        return result;
    }
}
