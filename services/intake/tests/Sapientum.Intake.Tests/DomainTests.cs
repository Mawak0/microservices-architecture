using Sapientum.Intake.Domain;
using Xunit;

namespace Sapientum.Intake.Tests;

public sealed class DomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T00:00:00Z");
    private static Admission OpenGame => new("game", 1, "running", Now.AddMinutes(10), false);

    [Fact]
    public void DeadlineIsExclusive()
    {
        Assert.True(OpenGame.AllowsSubmission(OpenGame.SubmissionDeadline.AddTicks(-1)));
        Assert.False(OpenGame.AllowsSubmission(OpenGame.SubmissionDeadline));
    }
    [Fact]
    public void LatePermissionOverridesExpiredDeadlineWithoutANewIdentity()
    {
        var game = OpenGame with { Version = 2, Status = "submission-closed", SubmissionDeadline = Now.AddDays(-1), LateSubmissionAllowed = true };
        var session = IntakeSession.Open(game, 2, Now);
        Assert.Equal("game", session.GameId); Assert.True(session.IsOpen);
        Assert.Equal(IntakeError.LateAdmissionActive, Assert.Throws<IntakeException>(() => session.Close(game, 2, Now)).Error);
    }
    [Fact]
    public void StalePermissionCannotCloseOrReopenSession()
    {
        var session = IntakeSession.Open(OpenGame, 1, Now);
        var closed = OpenGame with { Version = 2, SubmissionDeadline = Now.AddMinutes(-1) };
        Assert.Equal(IntakeError.AdmissionVersionMismatch, Assert.Throws<IntakeException>(() => session.Close(closed, 1, Now)).Error);
        session.Close(closed, 2, Now);
        Assert.Equal(IntakeError.AdmissionVersionMismatch, Assert.Throws<IntakeException>(() => session.Synchronize(OpenGame, 1, Now)).Error);
    }
    [Fact]
    public void ClosingDoesNotEraseReceiptAndSameClosureIsNotNewTransition()
    {
        var session = IntakeSession.Open(OpenGame, 1, Now);
        var receipt = Receipt.Accept("receipt", new("blank", "game", "team", "task", "7"), OpenGame, session, "image", Now);
        var closed = OpenGame with { Version = 2, SubmissionDeadline = Now.AddMinutes(-1) };
        Assert.True(session.Close(closed, 2, Now)); Assert.False(session.Close(closed, 2, Now));
        Assert.Equal("task", receipt.Blank.TaskId); Assert.Equal("7", receipt.Blank.TaskNumber);
    }
    [Fact]
    public void ReceiptCannotBeCreatedWithoutDurableImageOrMatchingGame()
    {
        var session = IntakeSession.Open(OpenGame, 1, Now);
        Assert.Throws<IntakeException>(() => Receipt.Accept("r", new("b", "game", "t", "task", "1"), OpenGame, session, "", Now));
        Assert.Throws<IntakeException>(() => Receipt.Accept("r", new("b", "other", "t", "task", "1"), OpenGame, session, "image", Now));
    }
    [Fact]
    public void TeamAdmissionCannotBeReusedFromAnotherGame()
    {
        var blank = new BlankBinding("b", "game", "team", "task", "1");
        Assert.Equal(IntakeError.TeamNotAdmitted, Assert.Throws<IntakeException>(() => new TeamAdmission("team", "another", true).RequireEligible(blank)).Error);
    }
    [Fact]
    public void ReceiptEventDoesNotLeakReferenceAnswersOrImageBytes()
    {
        var receipt = Receipt.Accept("r", new("b", "game", "team", "task", "1"), OpenGame, IntakeSession.Open(OpenGame, 1, Now), "image", Now);
        var message = ReceiptAccepted.From(receipt, "stable-event-id");
        Assert.Equal("task", message.Data.TaskId); Assert.Equal("stable-event-id", message.EventId);
        Assert.DoesNotContain(typeof(ReceiptAcceptedData).GetProperties(), p => p.Name is "ReferenceAnswer" or "ImageBase64" or "Score");
    }
}
