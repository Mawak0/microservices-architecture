using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
public sealed class ReceiveReceipt(ITransactions transactions, IAcceptedReceipts receipts, ITaskAnswers tasks)
{
    public async Task<Outcome<CheckView>> Execute(CommandIdentity identity, string eventId, Receipt incoming,
        CancellationToken cancellationToken)
    {
        var replay = await transactions.Replay<CheckView>(identity, cancellationToken);
        if (replay is not null) return replay;
        var existing = (await transactions.Run<CheckView?>(null,
            s => new(s.Checks.FindByReceipt(incoming.Id) is { } check ? CheckView.From(check) : null), cancellationToken)).Value;
        if (existing is not null)
            return await transactions.Run(identity, s =>
            {
                var current = s.Checks.FindByReceipt(incoming.Id)!;
                if (current.Receipt != incoming) throw new DomainFailure("RECEIPT_MISMATCH", FailureKind.Validation);
                var prior = s.Inbox.ReceiptForEvent(eventId);
                if (prior is not null && prior != incoming.Id) throw new DomainFailure("EVENT_ID_REUSED");
                s.Inbox.RememberReceipt(eventId, incoming.Id);
                return new Outcome<CheckView>(CheckView.From(current));
            }, cancellationToken);
        var trusted = await receipts.Get(incoming.Id, cancellationToken);
        if (trusted != incoming) throw new DomainFailure("RECEIPT_MISMATCH", FailureKind.Validation);
        var reference = await tasks.Get(trusted.TaskId, cancellationToken);
        return await transactions.Run(identity, s =>
        {
            var priorId = s.Inbox.ReceiptForEvent(eventId);
            if (priorId is not null && priorId != incoming.Id)
                throw new DomainFailure("EVENT_ID_REUSED");
            var duplicate = s.Checks.FindByReceipt(incoming.Id);
            if (duplicate is not null)
            {
                s.Inbox.RememberReceipt(eventId, incoming.Id);
                return new Outcome<CheckView>(CheckView.From(duplicate));
            }
            var check = AnswerCheck.Create(Guid.NewGuid().ToString(), trusted, reference);
            s.Checks.Save(check); s.Inbox.RememberReceipt(Required.Text(eventId), trusted.Id);
            s.Completions.Save(s.Completions.Get(trusted.GameId));
            return new Outcome<CheckView>(CheckView.From(check), true);
        }, cancellationToken);
    }
}
