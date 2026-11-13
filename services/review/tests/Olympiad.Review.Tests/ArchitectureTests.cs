using System.Reflection;
using System.Xml.Linq;
using Olympiad.Review.Application;
using Olympiad.Review.Domain;
using Xunit;
namespace Olympiad.Review.Tests;
public sealed class ArchitectureTests
{
    private static string Root()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Olympiad.Review.sln"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    [Fact]
    public void CompiledCoreAssembliesDependOnlyInwards()
    {
        Assert.All(typeof(AnswerCheck).Assembly.GetReferencedAssemblies(), a =>
            Assert.True(a.Name == "netstandard" || a.Name!.StartsWith("System", StringComparison.Ordinal)));
        var dependencies = typeof(ReceiveReceipt).Assembly.GetReferencedAssemblies();
        Assert.All(dependencies, a => Assert.True(a.Name!.StartsWith("System") || a.Name is "netstandard" or "Olympiad.Review.Domain", a.FullName));
        Assert.Contains(dependencies, a => a.Name == "Olympiad.Review.Domain");
    }
    [Fact]
    public void ProjectReferencesEnforceDependencyRule()
    {
        foreach (var layer in new[] { "Domain", "Application" })
        {
            var file = Path.Combine(Root(), "src", "Olympiad.Review." + layer, "Olympiad.Review." + layer + ".csproj");
            var project = XDocument.Load(file); Assert.Empty(project.Descendants("PackageReference"));
            var references = project.Descendants("ProjectReference").Select(e => e.Attribute("Include")!.Value).ToArray();
            if (layer == "Domain") Assert.Empty(references);
            else Assert.Equal(new[] { "../Olympiad.Review.Domain/Olympiad.Review.Domain.csproj" }, references);
        }
    }
    [Fact]
    public void CoreSourceHasNoTransportOrPersistenceTypes()
    {
        foreach (var layer in new[] { "Domain", "Application" })
            foreach (var path in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Olympiad.Review." + layer), "*.cs")
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            {
                var code = File.ReadAllText(path);
                foreach (var forbidden in new[] { "HttpClient", "JsonElement", "JsonObject", "Postgres", "IServiceProvider",
                    "IConfiguration", "HttpContext", "System.IO", "System.Text.Json", "DateTimeOffset.UtcNow" })
                    Assert.DoesNotContain(forbidden, code);
            }
    }
    [Fact]
    public void RichAggregatesHaveNoPublicStateSetters()
    {
        foreach (var type in new[] { typeof(AnswerCheck), typeof(Appeal), typeof(GameAppealQuota), typeof(GameReviewCompletion) })
            Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic ?? false, property.Name));
    }
    [Fact]
    public void ApiUsesInfrastructureOnlyAtComposition()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Olympiad.Review.Api"), "*.cs")
            .Where(path => Path.GetFileName(path) != "Program.cs"))
            Assert.DoesNotContain("Olympiad.Review.Infrastructure", File.ReadAllText(file));
    }
}
