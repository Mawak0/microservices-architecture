namespace Olympiad.Review.Domain;
public sealed record AppealSnapshot(string Id, string CheckId, string GameId, string TeamId,
    string TaskId, string Reason, bool Resolved, string? Outcome, bool UsageReturned);
public sealed class Appeal
{
    private AppealSnapshot state;
    private Appeal(AppealSnapshot state) => this.state = state;
    public string Id => state.Id;
    public string CheckId => state.CheckId;
    public string GameId => state.GameId;
    public string TeamId => state.TeamId;
    public string TaskId => state.TaskId;
    public bool Resolved => state.Resolved;
    public string? Outcome => state.Outcome;
    public static Appeal Open(string id, AnswerCheck check, string reason) =>
        new(new(Required.Text(id), check.Id, check.Receipt.GameId, check.Receipt.TeamId,
            check.Receipt.TaskId, Required.Text(reason), false, null, false));
    public static Appeal Restore(AppealSnapshot snapshot) => new(snapshot);
    public AppealSnapshot Snapshot() => state;
    public bool Resolve(string outcome)
    {
        if (Resolved) throw new DomainFailure("APPEAL_ALREADY_RESOLVED");
        if (outcome is not ("upheld" or "jury-error-corrected"))
            throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
        var returned = outcome == "jury-error-corrected";
        state = state with { Resolved = true, Outcome = outcome, UsageReturned = returned };
        return returned;
    }
}
public sealed record QuotaSnapshot(string GameId, int Limit, int Used);
public sealed class GameAppealQuota
{
    public string GameId { get; }
    public int Limit { get; private set; }
    public int Used { get; private set; }
    public int Remaining => Math.Max(0, Limit - Used);
    public GameAppealQuota(string gameId, int limit, int used = 0)
    {
        GameId = Required.Text(gameId);
        if (limit < 0 || used < 0) throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
        Limit = limit; Used = used;
    }
    public void SetLimit(int limit)
    {
        if (limit < 0) throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
        Limit = limit;
    }
    public void Use()
    {
        if (Remaining == 0) throw new DomainFailure("APPEAL_QUOTA_EXHAUSTED");
        Used++;
    }
    public void Return()
    {
        if (Used <= 0) throw new InvalidOperationException("Cannot return an unconsumed appeal.");
        Used--;
    }
    public QuotaSnapshot Snapshot() => new(GameId, Limit, Used);
}
public sealed record GameProfile(string Id, int AdmissionVersion, string Status,
    DateTimeOffset SubmissionDeadline, DateTimeOffset AppealDeadline,
    bool LateSubmissionAllowed, bool AppealsAllowed)
{
    public bool AcceptingSubmissions(DateTimeOffset now) =>
        LateSubmissionAllowed || (Status == "running" && now < SubmissionDeadline);
}
public sealed class AppealEligibility
{
    public void Require(GameProfile game, AnswerCheck check, DateTimeOffset now)
    {
        if (!game.AppealsAllowed) throw new DomainFailure("APPEAL_NOT_ALLOWED");
        if (now >= game.AppealDeadline) throw new DomainFailure("APPEAL_DEADLINE_PASSED");
        if (check.OriginalVerdict is null) throw new DomainFailure("CHECK_NOT_REVIEWED");
    }
}
