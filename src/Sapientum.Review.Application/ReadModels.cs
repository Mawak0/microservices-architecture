using Sapientum.Review.Domain;
namespace Sapientum.Review.Application;
public sealed record LeaseView(string Token, DateTimeOffset ExpiresAt);
public sealed record HistoryView(string Verdict, string ActorId, DateTimeOffset RecordedAt, string? Reason);
public sealed record CheckView(string Id, string ReceiptId, string BlankId, string GameId,
    int AdmissionVersion, string TeamId, string ImageRef, string TaskId, string TaskNumber,
    string ReferenceAnswer, string Status, string? AssignedTo, LeaseView? Lease,
    string? OriginalVerdict, string? Verdict, int Revision, IReadOnlyList<HistoryView> History)
{
    public static CheckView From(AnswerCheck c) => new(c.Id, c.Receipt.Id, c.Receipt.BlankId,
        c.Receipt.GameId, c.Receipt.AdmissionVersion, c.Receipt.TeamId, c.Receipt.ImageRef,
        c.Receipt.TaskId, c.Receipt.TaskNumber, c.ReferenceAnswer, c.Stage switch
        {
            CheckStage.Pending => "pending",
            CheckStage.Checking => "checking",
            CheckStage.Checked => "checked",
            CheckStage.AppealPending => "appeal-pending",
            _ => "appeal-checking"
        }, c.Assignment?.ActorId,
        c.Assignment is null ? null : new(c.Assignment.Token, c.Assignment.ExpiresAt),
        c.OriginalVerdict?.Wire(), c.Verdict?.Wire(), c.Revision,
        c.History.Select(h => new HistoryView(h.Verdict.Wire(), h.ActorId, h.RecordedAt, h.Reason)).ToArray());
}
public sealed record AppealView(string Id, string CheckId, string Status, string? Outcome, CheckView Check)
{
    public static AppealView From(Appeal appeal, AnswerCheck check) => new(appeal.Id, appeal.CheckId,
        appeal.Resolved ? "resolved" : check.Stage == CheckStage.AppealChecking ? "checking" : "pending",
        appeal.Outcome, CheckView.From(check));
}
public sealed record QuotaView(string GameId, int Limit, int Used, int Remaining)
{
    public static QuotaView From(GameAppealQuota quota) => new(quota.GameId, quota.Limit, quota.Used, quota.Remaining);
}
public static class SessionLookup
{
    public static AnswerCheck Check(this IReviewSession s, string id) =>
        s.Checks.Find(id) ?? throw new DomainFailure("NOT_FOUND", FailureKind.Missing);
    public static Appeal Appeal(this IReviewSession s, string id) =>
        s.Appeals.Find(id) ?? throw new DomainFailure("NOT_FOUND", FailureKind.Missing);
}
public sealed record LeaseSettings(TimeSpan Duration);
public static class OutgoingEvents
{
    public static void Verdict(IEventOutbox events, AnswerCheck c, string type, DateTimeOffset now) =>
        events.Add(new(Guid.NewGuid().ToString(), type, now, c.Id, new VerdictEventData(
            c.Receipt.GameId, c.Receipt.AdmissionVersion, c.Id, c.Receipt.Id, c.Receipt.BlankId,
            c.Receipt.TeamId, c.Receipt.TaskId, c.Verdict!.Value.Wire(), c.Revision)));
    public static void Appeal(IEventOutbox events, Appeal appeal, AnswerCheck c,
        string type, DateTimeOffset now) => events.Add(new(Guid.NewGuid().ToString(), type, now, appeal.Id,
            new AppealEventData(c.Receipt.GameId, c.Receipt.AdmissionVersion,
                appeal.Id, c.Id, c.Receipt.TeamId, c.Receipt.TaskId, appeal.Outcome)));
}
