using Olympiad.Review.Application;
namespace Olympiad.Review.Infrastructure;
internal sealed class IncomingEvents(DatabaseScope database) : IIncomingEvents
{
    public string? ReceiptForEvent(string eventId)
    {
        using var command = database.Command("SELECT receipt_id FROM incoming_receipts WHERE event_id=@id", ("@id", eventId));
        return command.ExecuteScalar() as string;
    }
    public void RememberReceipt(string eventId, string receiptId) => database.Execute("""
        INSERT INTO incoming_receipts(event_id,receipt_id) VALUES(@event,@receipt) ON CONFLICT(event_id) DO NOTHING
        """, ("@event", eventId), ("@receipt", receiptId));
}
