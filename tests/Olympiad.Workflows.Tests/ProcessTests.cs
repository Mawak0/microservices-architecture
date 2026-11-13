using Olympiad.Workflows.Domain;
using Xunit;
using Process = Olympiad.Workflows.Domain.Process;

namespace Olympiad.Workflows.Tests;

public sealed class ProcessTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    internal static Process New(string? checkpoint = null, string key = "key", string fingerprint = "hash") =>
        Process.Accept(Guid.NewGuid().ToString(), "observer", "POST:/answers", key, fingerprint,
            new("/answers", "POST", "/answers", "{}", "{}", "{}", [1, 2]), checkpoint, Now);
    internal static readonly ProcessResult Result = new(201, "application/json", [3, 4], "answer");

    [Fact]
    public void CompletionErasesRequestButPreservesResultAndIdentity()
    {
        var process = New();
        Assert.True(process.TryClaim("first", Now, TimeSpan.FromSeconds(10)));
        Assert.False(process.TryClaim("second", Now, TimeSpan.FromSeconds(10)));
        process.Succeed("first", Result, false, Now, TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessStatus.Completed, process.Snapshot.Status);
        Assert.Null(process.Snapshot.Invocation);
        Assert.Equal(Result.ResourceId, process.Snapshot.Result!.ResourceId);
        Assert.Equal(Result.Body, process.Snapshot.Result.Body);
        Assert.Equal("key", process.Snapshot.Key);
        Assert.False(process.TryClaim("third", Now.AddDays(1), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void CheckpointTimeoutResendsSameSavedCommandAndRejectsOldWorker()
    {
        var process = New("ReviewRegistered");
        process.TryClaim("old", Now, TimeSpan.FromSeconds(10));
        process.Succeed("old", Result, false, Now, TimeSpan.FromSeconds(5));
        Assert.NotNull(process.Snapshot.Invocation);
        Assert.False(process.TryClaim("new", Now.AddSeconds(4), TimeSpan.FromSeconds(10)));
        Assert.True(process.TryClaim("new", Now.AddSeconds(5), TimeSpan.FromSeconds(10)));
        Assert.Equal(2, process.Snapshot.Attempts);
        Assert.Throws<InvalidOperationException>(() => process.Succeed("old", Result, true, Now.AddSeconds(5), TimeSpan.Zero));
        Assert.False(process.Confirm("wrong", "answer"));
        Assert.False(process.Confirm("ReviewRegistered", "other"));
        Assert.True(process.Confirm("ReviewRegistered", "answer"));
        Assert.Null(process.Snapshot.Invocation);
        Assert.False(process.Confirm("ReviewRegistered", "answer"));
    }

    [Fact]
    public void CrashReleasesWorkOnlyAfterLeaseAndCompensationIsRetried()
    {
        var process = New();
        process.TryClaim("crashed", Now, TimeSpan.FromSeconds(5));
        Assert.True(process.TryClaim("replacement", Now.AddSeconds(5), TimeSpan.FromSeconds(5)));
        process.Reject("replacement", new(409, "application/json", [], "upload"), "CLOSED", true, Now.AddSeconds(5));
        Assert.Equal(ProcessStatus.Compensating, process.Snapshot.Status);
        Assert.NotNull(process.Snapshot.Invocation);
        process.TryClaim("cleanup", Now.AddSeconds(5), TimeSpan.FromSeconds(5));
        process.Retry("cleanup", "S3_DOWN", Now.AddSeconds(5), TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessStatus.Compensating, process.Snapshot.Status);
        Assert.False(process.TryClaim("early", Now.AddSeconds(6), TimeSpan.FromSeconds(5)));
        process.TryClaim("cleanup2", Now.AddSeconds(7), TimeSpan.FromSeconds(5));
        process.Compensated("cleanup2", Now.AddSeconds(7));
        Assert.Equal(ProcessStatus.Failed, process.Snapshot.Status);
        Assert.Null(process.Snapshot.Invocation);
    }

    [Fact]
    public void DefinitiveFailureAndFailedCheckpointAreTerminal()
    {
        var process = New("PdfReady");
        process.TryClaim("worker", Now, TimeSpan.FromSeconds(10));
        process.Succeed("worker", Result, false, Now, TimeSpan.FromSeconds(5));
        Assert.False(process.RejectConfirmation("other", "answer", "FAILED", Result));
        Assert.True(process.RejectConfirmation("PdfReady", "answer", "PDF_FAILED", Result));
        Assert.Equal(ProcessStatus.Failed, process.Snapshot.Status);
        Assert.Null(process.Snapshot.Invocation);
        Assert.False(process.RejectConfirmation("PdfReady", "answer", "FAILED", Result));
        var rejected = New(); rejected.TryClaim("worker", Now, TimeSpan.FromSeconds(10));
        rejected.Reject("worker", Result, "INVALID", false, Now);
        Assert.True(rejected.IsTerminal);
        Assert.Null(rejected.Snapshot.Invocation);
    }

    [Fact]
    public void InvalidIdentityLeaseAndExpiredCompletionAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Process.Accept("", "a", "s", "k", "f", new("h", "POST", "/", "{}", "{}", "{}", []), null, Now));
        var process = New();
        Assert.Throws<ArgumentException>(() => process.TryClaim("", Now, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => process.TryClaim("x", Now, TimeSpan.Zero));
        process.TryClaim("worker", Now, TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidOperationException>(() => process.Succeed("worker", Result, true, Now.AddSeconds(1), TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => process.Compensated("worker", Now));
        var snapshot = process.Snapshot; snapshot.Invocation!.Arguments[0] = 99;
        Assert.Equal(1, process.Snapshot.Invocation!.Arguments[0]);
    }

    [Fact]
    public void CallerCannotChangeSavedArgumentsOrResultThroughAnArrayReference()
    {
        var snapshot=New().Snapshot; var restored=Process.Restore(snapshot);
        snapshot.Invocation!.Arguments[0]=99;
        Assert.Equal(1,restored.Snapshot.Invocation!.Arguments[0]);
        restored.TryClaim("worker",Now,TimeSpan.FromSeconds(10));
        var result=new ProcessResult(200,"application/json",[3,4]);
        restored.Succeed("worker",result,false,Now,TimeSpan.Zero);
        result.Body[0]=99;
        Assert.Equal(3,restored.Snapshot.Result!.Body[0]);
    }
}
