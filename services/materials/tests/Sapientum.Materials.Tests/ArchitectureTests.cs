using System.Reflection;
using System.Xml.Linq;
using Sapientum.Materials.Application;
using Sapientum.Materials.Domain;
using Xunit;

namespace Sapientum.Materials.Tests;

public sealed class ArchitectureTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IMPLEMENTATION.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot locate architecture source.");
    }
    [Fact] public void DomainAndApplicationAssembliesOnlyReferenceInwardAndBcl()
    {
        var domain = typeof(AnswerBlank).Assembly; var application = typeof(IssueBlanks).Assembly;
        Assert.All(domain.GetReferencedAssemblies(), a => Assert.True(IsBcl(a), $"Domain depends on {a.Name}"));
        Assert.All(application.GetReferencedAssemblies(), a => Assert.True(IsBcl(a) || a.Name == domain.GetName().Name, $"Application depends on {a.Name}"));
    }
    private static bool IsBcl(AssemblyName assembly) => assembly.Name is "System.Runtime" or "System.Collections" or "System.Linq" or "System.Runtime.Extensions" or "System.Security.Cryptography" or "System.Text.Encoding.Extensions" or "System.Threading" or "System.Threading.Tasks" or "System.Console" or "netstandard";
    [Fact] public void ActualProjectsEnforceZeroPackagesInTheCoreAndTheDependencyRule()
    {
        var root = Root(); var domain = XDocument.Load(Path.Combine(root, "src/Sapientum.Materials.Domain/Sapientum.Materials.Domain.csproj"));
        var application = XDocument.Load(Path.Combine(root, "src/Sapientum.Materials.Application/Sapientum.Materials.Application.csproj"));
        Assert.Empty(domain.Descendants("PackageReference")); Assert.Empty(domain.Descendants("ProjectReference"));
        Assert.Empty(application.Descendants("PackageReference"));
        Assert.Equal("../Sapientum.Materials.Domain/Sapientum.Materials.Domain.csproj", Assert.Single(application.Descendants("ProjectReference")).Attribute("Include")?.Value);
    }
    [Fact] public void ApiNamesInfrastructureOnlyAtCompositionRoot()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src/Sapientum.Materials.Api"), "*.cs", SearchOption.TopDirectoryOnly))
            if (Path.GetFileName(file) != "CompositionRoot.cs") Assert.DoesNotContain("Sapientum.Materials.Infrastructure", File.ReadAllText(file));
    }
    [Fact] public void DomainHasNoPublicMutationAndPrintProjectionCannotStoreAnAnswer()
    {
        var types = typeof(AnswerBlank).Assembly.GetTypes().Where(t => t.IsClass && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)));
        foreach (var type in types)
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (property.SetMethod?.IsPublic == true)
                    Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit), property.SetMethod.ReturnParameter.GetRequiredCustomModifiers());
        foreach (var type in new[] { typeof(PrintableTask), typeof(PrintableBlank), typeof(PrintSnapshot), typeof(AnswerBlank) })
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("reference", StringComparison.OrdinalIgnoreCase));
    }
}
