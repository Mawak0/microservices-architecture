using System.Reflection;
using System.Xml.Linq;
using Sapientum.Review.Application;
using Sapientum.Review.Domain;
using Xunit;
namespace Sapientum.Review.Tests;
public sealed class ArchitectureTests
{
    private static string Root()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Sapientum.Review.sln"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    [Fact]
    public void CompiledCoreAssembliesDependOnlyInwards()
    {
        Assert.All(typeof(AnswerCheck).Assembly.GetReferencedAssemblies(), a => Assert.StartsWith("System", a.Name));
        var dependencies = typeof(ReceiveReceipt).Assembly.GetReferencedAssemblies();
        Assert.All(dependencies, a => Assert.True(a.Name!.StartsWith("System") || a.Name == "Sapientum.Review.Domain", a.FullName));
        Assert.Contains(dependencies, a => a.Name == "Sapientum.Review.Domain");
    }
    [Fact]
    public void ProjectReferencesEnforceDependencyRule()
    {
        foreach (var layer in new[] { "Domain", "Application" })
        {
            var file = Path.Combine(Root(), "src", "Sapientum.Review." + layer, "Sapientum.Review." + layer + ".csproj");
            var project = XDocument.Load(file); Assert.Empty(project.Descendants("PackageReference"));
            var references = project.Descendants("ProjectReference").Select(e => e.Attribute("Include")!.Value).ToArray();
            if (layer == "Domain") Assert.Empty(references);
            else Assert.Equal(new[] { "../Sapientum.Review.Domain/Sapientum.Review.Domain.csproj" }, references);
        }
    }
    [Fact]
    public void CoreSourceHasNoTransportOrPersistenceTypes()
    {
        foreach (var layer in new[] { "Domain", "Application" })
            foreach (var path in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Sapientum.Review." + layer), "*.cs")
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            {
                var code = File.ReadAllText(path);
                foreach (var forbidden in new[] { "HttpClient", "JsonElement", "JsonObject", "Sqlite", "IServiceProvider",
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
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Sapientum.Review.Api"), "*.cs")
            .Where(path => Path.GetFileName(path) != "Program.cs"))
            Assert.DoesNotContain("Sapientum.Review.Infrastructure", File.ReadAllText(file));
    }
}
