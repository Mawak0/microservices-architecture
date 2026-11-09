using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;
using Xunit;

namespace Olympiad.Materials.Tests;

public sealed class DomainTests
{
    [Fact] public void AssignedTaskAndCapabilityAreTheRulesRegardlessOfGameName()
    {
        var game = Sample.Game(false);
        Assert.Equal("2", game.RequireIssuableTask("task-2", null).TaskNumber);
        Assert.Equal("MODIFIER_NOT_ALLOWED", Assert.Throws<DomainException>(() => game.RequireIssuableTask("task-1", new(ModifierKind.Add, 3))).Code);
        Assert.Equal("TASK_NOT_IN_GAME", Assert.Throws<DomainException>(() => game.RequireIssuableTask("task-new", null)).Code);
    }
    [Fact] public void ABlankCannotBeReboundOrPrintedInAnotherScope()
    {
        var blank = Sample.Blank(); blank.EnsurePrintableIn("game", "team"); blank.EnsurePrintableIn("game", null);
        Assert.Equal("MATERIAL_SCOPE_MISMATCH", Assert.Throws<DomainException>(() => blank.EnsurePrintableIn("other", "team")).Code);
        Assert.Throws<DomainException>(() => blank.EnsurePrintableIn("game", "other"));
    }
    [Fact] public void KitRequiresExactlyItsOwnConfiguredNumberOfDistinctBlanks()
    {
        Assert.Throws<DomainException>(() => new TeamKit("kit", "game", "team", 2, 1, false, "layout", ["same", "same"]));
        Assert.Throws<DomainException>(() => new TeamKit("kit", "game", "team", 2, 1, false, "layout", ["one"]));
        Assert.Throws<DomainException>(() => TeamKit.Prepare("kit", Sample.Game(), new("team", "game", false, ""), ["a", "b", "c"]));
    }
    [Fact] public void TemplateAndProjectionRejectUnsafeOrUnidentifiableSheets()
    {
        Assert.Throws<DomainException>(() => new MaterialTemplate("layout", [new(TemplateBlockKind.Text, "no QR")], [new(TemplateBlockKind.Text, "task")]));
        Assert.Throws<DomainException>(() => new PrintSnapshot(Sample.Template(), new Dictionary<string, TemplateValue>(), [], []));
        Assert.Throws<DomainException>(() => new TemplateBlock(TemplateBlockKind.Field).Validate());
    }
    [Fact] public void SnapshotDefensivelyCopiesInputs()
    {
        var parameters = new Dictionary<string, TemplateValue> { ["heading"] = new(TemplateValueKind.Text, "original") };
        var blanks = new List<PrintableBlank> { new(Sample.Blank(), "team") };
        var snapshot = new PrintSnapshot(Sample.Template(), parameters, blanks, []);
        parameters["heading"] = new(TemplateValueKind.Text, "changed"); blanks.Clear();
        Assert.Equal("original", snapshot.Parameters["heading"].Display); Assert.Single(snapshot.Blanks);
    }
    [Fact] public void ExpiredPrintLeaseCanBeClaimedAndOldAttemptCannotCommit()
    {
        var now = DateTimeOffset.UtcNow; var job = new PrintJob("job", "game", "team", Sample.Snapshot());
        Assert.True(job.TryStart("first", now, TimeSpan.FromSeconds(1)));
        Assert.False(job.TryStart("second", now, TimeSpan.FromSeconds(1)));
        Assert.True(job.TryStart("second", now.AddSeconds(2), TimeSpan.FromSeconds(1)));
        Assert.Throws<DomainException>(() => job.Complete("first", "artifact"));
        job.Complete("second", "artifact"); Assert.Equal(PrintStatus.Completed, job.Status);
        Assert.False(job.TryStart("third", now.AddSeconds(4), TimeSpan.FromSeconds(1)));
    }
    [Fact] public void GlobalRolesAreSummedAndDoNotElevateUnrelatedCapabilities()
    {
        var multi = new Actor("multi", ["observer", "checker"]); multi.Require(MaterialPermission.Print);
        Assert.Throws<AccessDeniedException>(() => multi.Require(MaterialPermission.Issue));
        new Actor("integration", ["integration"]).Require(MaterialPermission.Verify);
        Assert.Throws<AccessDeniedException>(() => new Actor("checker", ["checker"]).Require(MaterialPermission.Read));
    }
}
