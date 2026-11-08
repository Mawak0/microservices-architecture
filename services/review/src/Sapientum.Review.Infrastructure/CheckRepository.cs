using Sapientum.Review.Application;
using Sapientum.Review.Domain;
namespace Sapientum.Review.Infrastructure;
internal sealed class CheckRepository(DatabaseScope database) : ICheckRepository
{
    public AnswerCheck? Find(string id) => Restore(database.Read<CheckSnapshot>(
        "SELECT snapshot FROM checks WHERE id=$id", ("$id", id)));
    public AnswerCheck? FindByReceipt(string receiptId) => Restore(database.Read<CheckSnapshot>(
        "SELECT snapshot FROM checks WHERE receipt_id=$id", ("$id", receiptId)));
    public IReadOnlyList<AnswerCheck> ForGame(string gameId) => database.ReadMany<CheckSnapshot>(
        "SELECT snapshot FROM checks WHERE game_id=$game ORDER BY rowid", ("$game", gameId))
        .Select(AnswerCheck.Restore).ToArray();
    public void Save(AnswerCheck check) => database.Execute("""
        INSERT INTO checks(id,receipt_id,game_id,snapshot) VALUES($id,$receipt,$game,$snapshot)
        ON CONFLICT(id) DO UPDATE SET snapshot=excluded.snapshot
        """, ("$id", check.Id), ("$receipt", check.Receipt.Id), ("$game", check.Receipt.GameId),
        ("$snapshot", DatabaseScope.Serialize(check.Snapshot())));
    private static AnswerCheck? Restore(CheckSnapshot? snapshot) => snapshot is null ? null : AnswerCheck.Restore(snapshot);
}
