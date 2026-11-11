using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;
using Olympiad.Storage.S3;

namespace Olympiad.Intake.Infrastructure.Images;

public sealed class S3ImageStorage(S3Objects objects) : IImageStorage, IImageReader
{
    public async Task<string> SaveAsync(string receiptId, string blankId, ReadOnlyMemory<byte> image, string mediaType, CancellationToken ct)
    {
        try
        {
            await objects.PutAsync(receiptId, image, mediaType, ct);
            return "/receipts/" + Uri.EscapeDataString(receiptId) + "/image";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is Amazon.S3.AmazonS3Exception or HttpRequestException or IOException or OperationCanceledException)
        { throw new IntakeException(IntakeError.StorageUnavailable, "Image storage could not confirm persistence."); }
    }
    public async Task<byte[]> ReadAsync(ImageReference image, CancellationToken ct) =>
        (await objects.ReadAsync(new(image.Key, image.ContentType, image.Size, image.Sha256), ct)).Bytes;
}
