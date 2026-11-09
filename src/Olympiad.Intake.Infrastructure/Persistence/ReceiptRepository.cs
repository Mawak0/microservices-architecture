using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Infrastructure.Persistence;

internal sealed class ReceiptRepository(SqliteScope db) : IReceiptRepository
{
    public async Task<Receipt?> FindAsync(string receiptId, CancellationToken ct)
    {
        await using var command = db.Command("SELECT blank_id,game_id,team_id,task_id,task_number,admission_version,image_ref,accepted_at FROM receipts WHERE id=$id", ("$id", receiptId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Receipt.Restore(receiptId,
            new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)),
            reader.GetInt32(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7))) : null;
    }
    public Task<bool> ExistsForBlankAsync(string blankId, CancellationToken ct) => db.ExistsAsync("SELECT 1 FROM receipts WHERE blank_id=$id", ct, ("$id", blankId));
    public async Task<IReadOnlyList<string>> ListIdsAsync(string gameId, CancellationToken ct)
    {
        await using var command = db.Command("SELECT id FROM receipts WHERE game_id=$id ORDER BY accepted_at,id", ("$id", gameId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }
    public Task AddAsync(Receipt receipt, ReadOnlyMemory<byte> verifiedImage, string mediaType, CancellationToken ct) => db.ExecuteAsync("""
        INSERT INTO receipts(id,blank_id,game_id,team_id,task_id,task_number,admission_version,image_ref,accepted_at,verified_image,media_type)
        VALUES($id,$blank,$game,$team,$task,$number,$version,$image,$accepted,$bytes,$media)
        """, ct, ("$id", receipt.Id), ("$blank", receipt.Blank.BlankId), ("$game", receipt.Blank.GameId),
        ("$team", receipt.Blank.TeamId), ("$task", receipt.Blank.TaskId), ("$number", receipt.Blank.TaskNumber),
        ("$version", receipt.AdmissionVersion), ("$image", receipt.ImageRef), ("$accepted", SqliteScope.Timestamp(receipt.AcceptedAt)),
        ("$bytes", verifiedImage.ToArray()), ("$media", mediaType));
}
