using Olympiad.Materials.Application;
using Olympiad.Storage.S3;

namespace Olympiad.Materials.Infrastructure.Pdf;

public sealed class S3PrintArtifacts(S3Objects objects) : IPrintArtifactStorage
{
    public async Task<PrintArtifact> SaveAsync(string jobId, string workerToken, byte[] bytes, CancellationToken ct)
    {
        try
        {
            var stored = await objects.PutAsync(jobId + ":" + workerToken, bytes, "application/pdf", ct);
            return new(stored.Key, stored.ContentType, stored.Size, stored.Sha256);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is Amazon.S3.AmazonS3Exception or IOException or HttpRequestException or OperationCanceledException)
        { throw new DependencyUnavailableException("PDF storage could not confirm persistence."); }
    }
    public async Task<byte[]> ReadAsync(PrintArtifact artifact, CancellationToken ct)
    {
        try { return (await objects.ReadAsync(new(artifact.Key, artifact.ContentType, artifact.Size, artifact.Sha256), ct)).Bytes; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is Amazon.S3.AmazonS3Exception or IOException or HttpRequestException or OperationCanceledException)
        { throw new DependencyUnavailableException("PDF storage is unavailable or inconsistent."); }
    }
}
