using Olympiad.Intake.Application;
using Olympiad.Storage.S3;

namespace Olympiad.Intake.Infrastructure.Persistence;

internal sealed class ReceiptImageVersions(PostgresScope db) : IReceiptImageVersions
{
    public async Task<int> LatestAsync(string receiptId, CancellationToken ct)
    {
        await using var c = db.Command("SELECT coalesce(max(version),0) FROM receipt_images WHERE receipt_id=@id", ("@id", receiptId));
        return (int)(await c.ExecuteScalarAsync(ct))!;
    }

    public async Task<ImageReference?> FindAsync(string receiptId, int version, CancellationToken ct)
    {
        await using var c = db.Command("""
            SELECT object_key,media_type,image_size,image_sha256,download_ref FROM receipt_images
            WHERE receipt_id=@id AND version=@version
            """, ("@id", receiptId), ("@version", version));
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetString(3),r.GetString(4)) : null;
    }

    public Task AddAsync(StoredRetake retake, string actor, DateTimeOffset uploadedAt, ReadOnlyMemory<byte> image,
        string mediaType, CancellationToken ct)
    {
        var digest = S3Objects.Digest(image.Span);
        return db.ExecuteAsync("""
            INSERT INTO receipt_images(receipt_id,version,request_id,uploaded_by,uploaded_at,object_key,media_type,image_size,image_sha256,download_ref)
            VALUES(@id,@version,@request,@actor,@at,@key,@media,@size,@digest,@ref)
            """, ct, ("@id", retake.ReceiptId),("@version", retake.ImageVersion),("@request",retake.Id),
            ("@actor",actor),("@at",uploadedAt.UtcDateTime),("@key",S3Objects.Key(retake.ReceiptId,digest)),
            ("@media",mediaType),("@size",image.Length),("@digest",digest),("@ref",retake.ImageRef));
    }
}
