namespace Olympiad.Review.Domain;

public sealed record PhotoRetake(string Id, string RequestedBy, DateTimeOffset RequestedAt,
    bool RequiresApproval, bool ResumeAppeal, string? ApprovedBy = null, string? ApprovalReason = null);

public sealed partial class AnswerCheck
{
    public void RequestRetake(string id, string actor, string? token, DateTimeOffset now, GameProfile game, bool admin)
    {
        if (Retake is not null) throw new DomainFailure("RETAKE_ALREADY_REQUESTED");
        var assigned = Stage is CheckStage.Checking or CheckStage.AppealChecking;
        if (assigned)
        {
            if (!admin || token is not null) RequireOwner(actor, Required.Text(token), now, admin);
        }
        else if (!admin || Stage is not (CheckStage.Pending or CheckStage.Checked or CheckStage.AppealPending))
            throw new DomainFailure("RETAKE_NOT_AVAILABLE");

        Retake = new(Required.Text(id), actor, now, now >= game.SubmissionDeadline || Verdict is not null,
            Stage is CheckStage.AppealChecking or CheckStage.AppealPending);
        Invalidate();
        Stage = CheckStage.AwaitingRetake;
    }

    public void ApproveRetake(string requestId, string admin, string reason)
    {
        var request = RequireRetake(requestId);
        if (request.ApprovedBy is null)
            Retake = request with { ApprovedBy = admin, ApprovalReason = Required.Text(reason) };
    }

    public bool ApplyRetake(string requestId, string receiptId, string blankId, string gameId, int imageVersion)
    {
        if (receiptId != Receipt.Id || blankId != Receipt.BlankId || gameId != Receipt.GameId || imageVersion < 2)
            throw new DomainFailure("RETAKE_MISMATCH", FailureKind.Validation);
        if (imageVersion <= Receipt.ImageVersion) return false;
        var request = RequireRetake(requestId);
        if (imageVersion != Receipt.ImageVersion + 1) throw new DomainFailure("RETAKE_VERSION_CONFLICT");
        Receipt = Receipt with
        {
            ImageVersion = imageVersion,
            ImageRef = $"/receipts/{Receipt.Id}/image/versions/{imageVersion}"
        };
        Verdict = null;
        Retake = null;
        Stage = request.ResumeAppeal ? CheckStage.AppealPending : CheckStage.Pending;
        return true;
    }

    public void CorrectOwn(string actor, int expectedRevision, Verdict verdict, DateTimeOffset now, bool hasAppeal)
    {
        RequireRevision(expectedRevision);
        if (hasAppeal || Stage != CheckStage.Checked || Verdict is null || history.LastOrDefault()?.ActorId != actor)
            throw new DomainFailure("SELF_CORRECTION_NOT_AVAILABLE");
        if (Verdict == verdict) throw new DomainFailure("VERDICT_UNCHANGED", FailureKind.Validation);
        Append(verdict, actor, now, null);
    }

    public void RequireRevision(int expected)
    {
        if (expected != Revision) throw new DomainFailure("REVISION_CONFLICT");
    }

    private PhotoRetake RequireRetake(string id) =>
        Retake is { } request && request.Id == id ? request : throw new DomainFailure("RETAKE_NOT_AVAILABLE");
}
