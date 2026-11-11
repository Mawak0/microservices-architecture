using System.Reflection;
using System.Xml.Linq;
using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;
using Xunit;

namespace Olympiad.Intake.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void DomainAssemblyReferencesOnlyBcl()
    {
        Assert.All(typeof(Receipt).Assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name!.StartsWith("System", StringComparison.Ordinal) || reference.Name == "netstandard"));
    }
    [Fact]
    public void ApplicationAssemblyReferencesOnlyDomainAndBcl()
    {
        Assert.All(typeof(AcceptBlank).Assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name!.StartsWith("System", StringComparison.Ordinal) || reference.Name is "Olympiad.Intake.Domain" or "netstandard"));
    }
    [Fact]
    public void AggregatesHaveNoPublicMutationSetters()
    {
        foreach (var type in new[] { typeof(Receipt), typeof(IntakeSession) })
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => property.SetMethod?.IsPublic == true);
    }
    [Fact]
    public void ProjectAndSourceDependencyRuleIsEnforced()
    {
        var root = FindRoot();
        foreach (var layer in new[] { "Domain", "Application" })
        {
            var directory = Path.Combine(root, "src", $"Olympiad.Intake.{layer}");
            var project = XDocument.Load(Path.Combine(directory, $"Olympiad.Intake.{layer}.csproj"));
            Assert.Empty(project.Descendants("PackageReference"));
            var references = project.Descendants("ProjectReference").Select(e => e.Attribute("Include")!.Value).ToArray();
            if (layer == "Domain") Assert.Empty(references); else Assert.Equal(new[] { "../Olympiad.Intake.Domain/Olympiad.Intake.Domain.csproj" }, references);
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            {
                var source = File.ReadAllText(file);
                foreach (var forbidden in new[] { "HttpClient", "JsonSerializer", "JsonObject", "Npgsql", "IServiceProvider", "System.IO", "Microsoft.AspNetCore" })
                    Assert.DoesNotContain(forbidden, source);
            }
        }
        var api = Path.Combine(root, "src", "Olympiad.Intake.Api");
        foreach (var file in Directory.EnumerateFiles(api, "*.cs").Where(p => Path.GetFileName(p) is not ("Composition.cs" or "Program.cs")))
            Assert.DoesNotContain("Olympiad.Intake.Infrastructure", File.ReadAllText(file));
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Olympiad.Intake.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Solution root not found.");
    }
}
