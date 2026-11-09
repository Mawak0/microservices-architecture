using Olympiad.Materials.Application;
using Olympiad.Materials.Domain;
using Olympiad.Materials.Infrastructure.Http;

namespace Olympiad.Materials.Infrastructure.Templates;

public sealed class HttpTemplateRepository(DependencyClient client) : ITemplateRepository
{
    public async Task<MaterialTemplate> GetAsync(string key, CancellationToken cancellationToken)
    {
        var template = await client.GetAsync<TemplateDto>($"material-templates/{Uri.EscapeDataString(key)}", cancellationToken);
        return TemplateDocumentMapper.Map(template, key);
    }
}

internal static class TemplateDocumentMapper
{
    public static MaterialTemplate Map(TemplateDto template, string key)
    {
        if (template.Format != "declarative-v1" || template.Key != key) throw new DependencyUnavailableException("Unsupported template format or identity.");
        return new(template.Key, template.AnswerSheet.Select(Parse), template.TaskSheet.Select(Parse));
    }
    private static TemplateBlock Parse(BlockDto block) => block.Kind switch
    {
        "text" => new(TemplateBlockKind.Text, block.Text ?? throw new DependencyUnavailableException("Template text missing.")),
        "qr" => new(TemplateBlockKind.Qr),
        "answer-area" => new(TemplateBlockKind.AnswerArea),
        "field" => new(TemplateBlockKind.Field, Field: block.Field switch
        {
            "heading" => TemplateField.Heading, "taskNumber" => TemplateField.TaskNumber, "statement" => TemplateField.Statement,
            "blankNumber" => TemplateField.BlankNumber, "teamName" => TemplateField.TeamName, "gameTitle" => TemplateField.GameTitle,
            "modifierLabel" => TemplateField.ModifierLabel, _ => throw new DependencyUnavailableException("Unsupported template field.")
        }),
        _ => throw new DependencyUnavailableException("Unsupported template block.")
    };
}
internal sealed record TemplateDto(string Key, string Format, BlockDto[] AnswerSheet, BlockDto[] TaskSheet);
internal sealed record BlockDto(string Kind, string? Text, string? Field);
