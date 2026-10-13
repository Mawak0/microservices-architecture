namespace Sapientum.Review.Domain;
public enum CheckStage { Pending, Checking, Checked, AppealPending, AppealChecking }
public sealed record Receipt(string Id, string BlankId, string GameId, int AdmissionVersion,
    string TeamId, string TaskId, string TaskNumber, string ImageRef, DateTimeOffset AcceptedAt);
public sealed record Assignment(string Token, string ActorId, DateTimeOffset ExpiresAt);
public sealed record ExpiredAssignment(string Token, bool Expired);
public sealed record VerdictRecord(Verdict Verdict, string ActorId, DateTimeOffset RecordedAt, string? Reason);
public sealed record CheckSnapshot(string Id, Receipt Receipt, string ReferenceAnswer, CheckStage Stage, Assignment? Assignment,
    IReadOnlyList<ExpiredAssignment> PreviousAssignments, Verdict? OriginalVerdict,
    Verdict? Verdict, IReadOnlyList<VerdictRecord> History);

/// <summary>One received answer and its fenced ownership; owns every verdict revision.</summary>
public sealed class AnswerCheck
{
    private readonly List<ExpiredAssignment> previous;
    private readonly List<VerdictRecord> history;
    public string Id { get; }
    public Receipt Receipt { get; }
    public string ReferenceAnswer { get; }
    public CheckStage Stage { get; private set; }
    public Assignment? Assignment { get; private set; }
    public Verdict? OriginalVerdict { get; private set; }
    public Verdict? Verdict { get; private set; }
    public int Revision => history.Count;
    public IReadOnlyList<VerdictRecord> History => history.AsReadOnly();
    private AnswerCheck(CheckSnapshot snapshot)
    {
        Id = snapshot.Id; Receipt = snapshot.Receipt; ReferenceAnswer = snapshot.ReferenceAnswer; Stage = snapshot.Stage;
        Assignment = snapshot.Assignment; OriginalVerdict = snapshot.OriginalVerdict; Verdict = snapshot.Verdict;
        previous = [.. snapshot.PreviousAssignments]; history = [.. snapshot.History];
    }
    public static AnswerCheck Create(string id, Receipt receipt, string referenceAnswer)
    {
        Required.Text(id); Required.Text(receipt.Id); Required.Text(receipt.BlankId);
        Required.Text(receipt.GameId); Required.Text(receipt.TeamId); Required.Text(receipt.TaskId);
        Required.Text(receipt.TaskNumber); Required.Text(receipt.ImageRef);
        if (receipt.AdmissionVersion < 1) throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
        return new(new(id, receipt, referenceAnswer, CheckStage.Pending, null, [], null, null, []));
    }
    public static AnswerCheck Restore(CheckSnapshot snapshot) => new(snapshot);
    public CheckSnapshot Snapshot() => new(Id, Receipt, ReferenceAnswer, Stage, Assignment, previous.ToArray(),
        OriginalVerdict, Verdict, history.ToArray());
    public bool Expire(DateTimeOffset now)
    {
        if (Assignment is null || now < Assignment.ExpiresAt) return false;
        previous.Add(new(Assignment.Token, true)); Assignment = null;
        Stage = Stage == CheckStage.AppealChecking ? CheckStage.AppealPending : CheckStage.Pending;
        return true;
    }
    public void Claim(string actorId, string token, DateTimeOffset now, TimeSpan duration, bool appeal)
    {
        Expire(now);
        var waiting = appeal ? CheckStage.AppealPending : CheckStage.Pending;
        if (Stage != waiting || Assignment is not null) throw new DomainFailure("CHECK_ALREADY_ASSIGNED");
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Assignment = new(Required.Text(token), Required.Text(actorId), now.Add(duration));
        Stage = appeal ? CheckStage.AppealChecking : CheckStage.Checking;
    }
    public void Renew(string actorId, string token, DateTimeOffset now, TimeSpan duration)
    {
        RequireOwner(actorId, token, now);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Assignment = Assignment! with { ExpiresAt = now.Add(duration) };
    }
    public void Release(string actorId, string token, DateTimeOffset now)
    {
        RequireOwner(actorId, token, now); Invalidate();
        Stage = Stage == CheckStage.AppealChecking ? CheckStage.AppealPending : CheckStage.Pending;
    }
    public void Record(Verdict verdict, string actorId, string token, DateTimeOffset now)
    {
        RequireOwner(actorId, token, now);
        if (Stage != CheckStage.Checking) throw new DomainFailure("CHECK_ALREADY_ASSIGNED");
        Append(verdict, actorId, now, null); OriginalVerdict = verdict;
        Invalidate(); Stage = CheckStage.Checked;
    }
    public void OpenAppeal()
    {
        if (OriginalVerdict is null || Stage != CheckStage.Checked) throw new DomainFailure("CHECK_NOT_REVIEWED");
        Stage = CheckStage.AppealPending;
    }
    public void ResolveAppeal(bool corrected, Verdict? verdict, string actorId, string token,
        DateTimeOffset now, string reason)
    {
        RequireOwner(actorId, token, now);
        if (Stage != CheckStage.AppealChecking) throw new DomainFailure("CHECK_NOT_REVIEWED");
        Required.Text(reason);
        if (corrected)
        {
            if (verdict is null) throw new DomainFailure("CORRECTION_REQUIRED", FailureKind.Validation);
            if (verdict == Verdict) throw new DomainFailure("VERDICT_UNCHANGED", FailureKind.Validation);
            Append(verdict.Value, actorId, now, reason);
        }
        Invalidate(); Stage = CheckStage.Checked;
    }
    public void Correct(Verdict verdict, string actorId, DateTimeOffset now, string reason)
    {
        if (OriginalVerdict is null) throw new DomainFailure("CHECK_NOT_REVIEWED");
        Append(verdict, actorId, now, Required.Text(reason));
        var appealed = Stage is CheckStage.AppealChecking or CheckStage.AppealPending;
        Invalidate(); Stage = appealed ? CheckStage.AppealPending : CheckStage.Checked;
    }
    private void Append(Verdict verdict, string actor, DateTimeOffset at, string? reason)
    {
        Verdict = verdict; history.Add(new(verdict, Required.Text(actor), at, reason));
    }
    private void Invalidate()
    {
        if (Assignment is not null) previous.Add(new(Assignment.Token, false));
        Assignment = null;
    }
    private void RequireOwner(string actor, string token, DateTimeOffset now)
    {
        Required.Text(token);
        if (Assignment?.Token != token)
        {
            var old = previous.LastOrDefault(p => p.Token == token);
            throw new DomainFailure(old is { Expired: true } && Assignment is null && previous.LastOrDefault()?.Token == token
                ? "ASSIGNMENT_EXPIRED" : "STALE_ASSIGNMENT");
        }
        if (now >= Assignment.ExpiresAt) throw new DomainFailure("ASSIGNMENT_EXPIRED");
        if (Assignment.ActorId != actor) throw new DomainFailure("CHECK_NOT_ASSIGNED_TO_YOU");
    }
}
