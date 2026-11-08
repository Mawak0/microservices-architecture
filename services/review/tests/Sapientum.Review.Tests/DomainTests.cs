using Sapientum.Review.Domain;
using Xunit;
namespace Sapientum.Review.Tests;

public sealed class DomainTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T00:00:00Z");
    internal static Receipt Receipt(string id = "receipt") => new(id, "blank-" + id, "game", 1,
        "team", "task", "1", "image://" + id, Now);
    internal static AnswerCheck Check(string id = "check") => AnswerCheck.Create(id, Receipt(id), "4");
    internal static GameProfile Game(int version = 1, bool late = false, bool appeals = false) =>
        new("game", version, "submission-closed", Now.AddHours(-1), Now.AddHours(-1), late, appeals);
    private static void Code(string code, Action action) => Assert.Equal(code, Assert.Throws<DomainFailure>(action).Code);

    [Fact]
    public void ExpiryAndReassignmentFenceOldTokenEvenForSameActor()
    {
        var c = Check(); c.Claim("actor", "first", Now, TimeSpan.FromMinutes(1), false);
        Code("ASSIGNMENT_EXPIRED", () => c.Record(Verdict.Correct, "actor", "first", Now.AddMinutes(1)));
        c.Expire(Now.AddMinutes(1)); var restored = AnswerCheck.Restore(c.Snapshot());
        restored.Claim("actor", "second", Now.AddMinutes(2), TimeSpan.FromMinutes(1), false);
        Code("STALE_ASSIGNMENT", () => restored.Record(Verdict.Correct, "actor", "first", Now.AddMinutes(2)));
        restored.Record(Verdict.Correct, "actor", "second", Now.AddMinutes(2));
        Code("STALE_ASSIGNMENT", () => restored.Release("actor", "first", Now.AddMinutes(2)));
    }
    [Fact]
    public void RenewalPreservesTokenAndUsesCurrentServerTime()
    {
        var c = Check(); c.Claim("actor", "token", Now, TimeSpan.FromMinutes(1), false);
        c.Renew("actor", "token", Now.AddSeconds(30), TimeSpan.FromMinutes(1));
        Assert.Equal("token", c.Assignment!.Token); Assert.Equal(Now.AddSeconds(90), c.Assignment.ExpiresAt);
        Code("CHECK_NOT_ASSIGNED_TO_YOU", () => c.Renew("other", "token", Now, TimeSpan.FromMinutes(1)));
    }
    [Fact]
    public void AdministratorCancelsActiveAppealAssignmentButDoesNotResolveAppeal()
    {
        var c = Checked(); c.OpenAppeal(); c.Claim("actor", "appeal", Now, TimeSpan.FromMinutes(1), true);
        c.Correct(Verdict.Correct, "admin", Now, "administrative correction");
        Assert.Equal(CheckStage.AppealPending, c.Stage); Assert.Null(c.Assignment);
        Code("STALE_ASSIGNMENT", () => c.ResolveAppeal(false, null, "actor", "appeal", Now, "review"));
        Assert.Equal(Verdict.Incorrect, c.OriginalVerdict); Assert.Equal(2, c.Revision);
    }
    [Fact]
    public void JuryCorrectionRequiresDifferentVerdict()
    {
        var c = Checked(); c.OpenAppeal(); c.Claim("actor", "appeal", Now, TimeSpan.FromMinutes(1), true);
        Code("CORRECTION_REQUIRED", () => c.ResolveAppeal(true, null, "actor", "appeal", Now, "review"));
        Code("VERDICT_UNCHANGED", () => c.ResolveAppeal(true, Verdict.Incorrect, "actor", "appeal", Now, "review"));
        Assert.Equal(1, c.Revision); Assert.Equal(CheckStage.AppealChecking, c.Stage);
    }
    [Fact]
    public void AppealCanReturnUsageOnlyOnce()
    {
        var appeal = Appeal.Open("appeal", Checked(), "request");
        Assert.True(appeal.Resolve("jury-error-corrected"));
        Code("APPEAL_ALREADY_RESOLVED", () => appeal.Resolve("jury-error-corrected"));
    }
    [Fact]
    public void QuotaReductionNeverErasesAlreadyConsumedUsage()
    {
        var quota = new GameAppealQuota("game", 2); quota.Use(); quota.Use(); quota.SetLimit(1);
        Assert.Equal(2, quota.Used); Assert.Equal(0, quota.Remaining);
        Code("APPEAL_QUOTA_EXHAUSTED", quota.Use); quota.Return(); Assert.Equal(0, quota.Remaining);
        quota.Return(); Assert.Equal(1, quota.Remaining);
    }


    [Fact]
    public void AppealEligibilityUsesCapabilitiesNeverGameNames()
    {
        var eligibility = new AppealEligibility(); var c = Checked();
        eligibility.Require(Game(appeals: true) with { AppealDeadline = Now.AddMinutes(1) }, c, Now);
        Code("APPEAL_NOT_ALLOWED", () => eligibility.Require(Game(), c, Now));
        Code("APPEAL_DEADLINE_PASSED", () => eligibility.Require(Game(appeals: true), c, Now));
    }
    internal static AnswerCheck Checked()
    {
        var c = Check(); c.Claim("actor", "first", Now, TimeSpan.FromMinutes(1), false);
        c.Record(Verdict.Incorrect, "actor", "first", Now); return c;
    }
}
