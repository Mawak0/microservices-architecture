using System.Text.Json;
using System.Text.Json.Serialization;
using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Api;

internal sealed record KitRequest(string GameId, string TeamId);
internal sealed record ModifierRequest([property: JsonRequired] string Kind, [property: JsonRequired] decimal Value);
internal sealed record IssueRequest(string GameId, string TeamId, string TaskId, int Count, ModifierRequest? Modifier);
internal sealed record PrintRequest(string GameId, string? TeamId, string[] BlankIds, string[] TaskIds);
internal sealed record VerificationRequest(string QrToken);
internal static class Transport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static async Task<T> ReadAsync<T>(HttpContext context) where T : class =>
        await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json, context.RequestAborted)
        ?? throw new JsonException("A JSON object is required.");
    public static string Required(string? value) => !string.IsNullOrWhiteSpace(value) ? value : throw new DomainException("VALIDATION_ERROR", "Identifiers and required fields must not be empty.");
    public static ScoreModifier? Modifier(ModifierRequest? request) => request is null ? null : new(request.Kind switch
    {
        "add" => ModifierKind.Add, "multiply" => ModifierKind.Multiply,
        _ => throw new DomainException("VALIDATION_ERROR", "Unknown modifier kind.")
    }, request.Value);
    public static object Blank(AnswerBlank blank) => new { blank.Id, blank.Number, blank.GameId, blank.TeamId, blank.TaskId, blank.TaskNumber,
        blank.QrToken, modifier = blank.Modifier is null ? null : new { kind = blank.Modifier.Kind == ModifierKind.Add ? "add" : "multiply", value = blank.Modifier.Value } };
    public static object Kit(TeamKit kit) => new { kit.Id, kit.GameId, kit.TeamId, kit.AnswerBlankCount, kit.TaskCardCount, kit.TaskCardsShared, kit.BlankIds, kit.TemplateKey };
    public static object Job(PrintJob job) => new { job.Id, job.GameId, job.TeamId, status = job.Status.ToString().ToLowerInvariant(),
        job.ArtifactUrl, job.Error, job.Manifest, templateKey = job.Snapshot.Template.Key,
        templateParameters = job.Snapshot.Parameters.ToDictionary(p => p.Key, p => Scalar(p.Value)) };
    private static object? Scalar(TemplateValue value) => value.Kind switch
    {
        TemplateValueKind.Text => value.Text, TemplateValueKind.Number => value.Number, TemplateValueKind.Boolean => value.Boolean, _ => null
    };
}
