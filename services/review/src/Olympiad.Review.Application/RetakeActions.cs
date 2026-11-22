using Olympiad.Review.Domain;

namespace Olympiad.Review.Application;

public sealed record RetakeTicket(string Id, string CheckId, string ReceiptId, string BlankId, string GameId,
    string TeamId, string TaskNumber, int ImageVersion, bool RequiresApproval, bool Approved,
    DateTimeOffset RequestedAt)
{
    public static RetakeTicket? From(AnswerCheck check) => check.Retake is { } request
        ? new(request.Id, check.Id, check.Receipt.Id, check.Receipt.BlankId, check.Receipt.GameId,
            check.Receipt.TeamId, check.Receipt.TaskNumber, check.Receipt.ImageVersion,
            request.RequiresApproval, request.ApprovedBy is not null, request.RequestedAt)
        : null;
}
public sealed record RetakeDelivery(string RetakeId, string CheckId, string ReceiptId, string BlankId,
    string GameId, int ImageVersion, int AdmissionVersion);
public sealed record RetakeAcknowledgment(string Id, string ReceiptId, int ImageVersion);

public sealed class RetakeActions(ITransactions transactions, IGameProfiles games, IClock clock)
{
    public async Task<Outcome<RetakeTicket>> Request(CommandIdentity identity, string checkId, string? token,
        bool admin, int expectedRevision, CancellationToken ct)
    {
        var replay = await transactions.Replay<RetakeTicket>(identity, ct);
        if (replay is not null) return replay;
        var gameId = (await transactions.Run<string>(null, s => new(s.Check(checkId).Receipt.GameId), ct)).Value;
        var game = await games.Get(gameId, ct);
        var now = await clock.Now(ct);
        return await transactions.Run(identity, s =>
        {
            RequireMutable(s, gameId);
            if (game.Status is "completed" or "published" or "finished") throw new DomainFailure("REVIEW_FINALIZED");
            var check = s.Check(checkId);
            check.RequireRevision(expectedRevision);
            check.RequestRetake(Guid.NewGuid().ToString(), identity.ActorId, token, now, game, admin);
            s.Checks.Save(check);
            return new Outcome<RetakeTicket>(RetakeTicket.From(check)!, true);
        }, ct);
    }

    public Task<Outcome<RetakeTicket>> Approve(CommandIdentity identity, string checkId, string requestId,
        string reason, CancellationToken ct) => transactions.Run(identity, s =>
    {
        var check = s.Check(checkId);
        RequireMutable(s, check.Receipt.GameId);
        check.ApproveRetake(requestId, identity.ActorId, reason);
        s.Checks.Save(check);
        return new Outcome<RetakeTicket>(RetakeTicket.From(check)!);
    }, ct);

    public async Task<RetakeTicket> Get(string checkId, string requestId, CancellationToken ct) =>
        (await transactions.Run<RetakeTicket>(null, s =>
        {
            var ticket = RetakeTicket.From(s.Check(checkId));
            if (ticket is null || ticket.Id != requestId) throw new DomainFailure("RETAKE_NOT_AVAILABLE", FailureKind.Missing);
            return new(ticket);
        }, ct)).Value;

    public async Task<Outcome<RetakeAcknowledgment>> Apply(CommandIdentity identity, RetakeDelivery delivery,
        CancellationToken ct)
    {
        var now = await clock.Now(ct);
        return await transactions.Run(identity, s =>
        {
            var check = s.Check(delivery.CheckId);
            if (check.Receipt.AdmissionVersion != delivery.AdmissionVersion)
                throw new DomainFailure("RETAKE_MISMATCH", FailureKind.Validation);
            var previousVerdict = check.Verdict;
            if (check.ApplyRetake(delivery.RetakeId, delivery.ReceiptId, delivery.BlankId, delivery.GameId, delivery.ImageVersion))
            {
                RequireMutable(s, check.Receipt.GameId);
                s.Checks.Save(check);
                if (previousVerdict is not null)
                    s.Events.Add(new(Guid.NewGuid().ToString(), "VerdictWithdrawnForRetake", now, check.Id,
                        new WithdrawnVerdictData(check.Receipt.GameId, check.Receipt.AdmissionVersion, check.Id,
                            check.Receipt.Id, check.Receipt.BlankId, check.Receipt.TeamId, check.Receipt.TaskId,
                            check.Revision, check.Receipt.ImageVersion)));
            }
            return new Outcome<RetakeAcknowledgment>(new(delivery.RetakeId, delivery.ReceiptId, delivery.ImageVersion));
        }, ct);
    }

    internal static void RequireMutable(IReviewSession session, string gameId)
    {
        if (session.Completions.Get(gameId).Snapshot().Published) throw new DomainFailure("REVIEW_FINALIZED");
    }
}

public sealed record WithdrawnVerdictData(string GameId, int AdmissionVersion, string CheckId, string ReceiptId,
    string BlankId, string TeamId, string TaskId, int Revision, int ImageVersion) : EventData(GameId, AdmissionVersion);

public sealed class SelfCorrectVerdict(ITransactions transactions, IClock clock)
{
    public async Task<Outcome<CheckView>> Execute(CommandIdentity identity, string id, int expectedRevision,
        Verdict verdict, CancellationToken ct)
    {
        var replay = await transactions.Replay<CheckView>(identity, ct);
        if (replay is not null) return replay;
        var now = await clock.Now(ct);
        return await transactions.Run(identity, s =>
        {
            var check = s.Check(id);
            var receipt = check.Receipt;
            RetakeActions.RequireMutable(s, receipt.GameId);
            var hasAppeal = s.Appeals.Exists(receipt.GameId, receipt.TeamId, receipt.TaskId);
            check.CorrectOwn(identity.ActorId, expectedRevision, verdict, now, hasAppeal);
            s.Checks.Save(check);
            OutgoingEvents.Verdict(s.Events, check, "VerdictCorrected", now);
            return new Outcome<CheckView>(CheckView.From(check));
        }, ct);
    }
}

public interface IRetakeWorklist
{
    Task<IReadOnlyList<RetakeTicket>> List(string gameId, int offset, int limit, CancellationToken ct);
}
