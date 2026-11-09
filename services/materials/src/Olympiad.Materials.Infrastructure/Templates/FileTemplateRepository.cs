using System.Text.Json;
using System.Text.Json.Serialization;
using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Infrastructure.Templates;

/// <summary>Materials-owned declarative catalog; no additional business service is required.</summary>
public sealed class FileTemplateRepository : ITemplateRepository
{
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public FileTemplateRepository(string directory) => this.directory = Path.GetFullPath(directory);
    public async Task<MaterialTemplate> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (key.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new DomainException("VALIDATION_ERROR", "Invalid template key.");
        var path = Path.GetFullPath(Path.Combine(directory, key.Replace('/', Path.DirectorySeparatorChar) + ".json"));
        if (!path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new DomainException("VALIDATION_ERROR", "Template path must stay in the owned catalog.");
        if (!File.Exists(path)) throw new NotFoundException("The selected material template is not registered.");
        try
        {
            await using var stream = File.OpenRead(path);
            var template = await JsonSerializer.DeserializeAsync<TemplateDto>(stream, Json, cancellationToken) ?? throw new DependencyUnavailableException("The template file is empty.");
            return TemplateDocumentMapper.Map(template, key);
        }
        catch (JsonException exception) { throw new DependencyUnavailableException("The configured template is invalid.", exception); }
        catch (IOException exception) { throw new DependencyUnavailableException("The configured template cannot be read.", exception); }
    }
}
