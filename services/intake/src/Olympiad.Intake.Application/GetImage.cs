using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Application;

public sealed class GetImage(IIntakeUnitOfWork store, IImageReader images)
{
    public async Task<(byte[] Bytes, string ContentType)> ExecuteAsync(string receiptId, CancellationToken ct, int? version = null)
    {
        var metadata = await store.ExecuteAsync((tx, token) => version is { } number ? tx.Images.FindAsync(receiptId, number, token) : tx.Receipts.ImageAsync(receiptId, token), ct)
            ?? throw new IntakeException(IntakeError.NotFound, "Accepted image not found.");
        return (await images.ReadAsync(metadata, ct), metadata.ContentType);
    }
}
