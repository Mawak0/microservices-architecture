using System.Text.Json;
using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Infrastructure.Persistence;

// This adapter-specific document is an immutable print snapshot, not an aggregate database or business dispatcher.
internal sealed record StoredBlank(string Id, long Number, string GameId, string TeamId, string TaskId, string TaskNumber,
    string QrToken, ScoreModifier? Modifier, string TeamName)
{
    public PrintableBlank Restore() => new(new(Id, Number, GameId, TeamId, TaskId, TaskNumber, QrToken, Modifier), TeamName);
    public static StoredBlank From(PrintableBlank b) => new(b.Blank.Id, b.Blank.Number, b.Blank.GameId, b.Blank.TeamId,
        b.Blank.TaskId, b.Blank.TaskNumber, b.Blank.QrToken, b.Blank.Modifier, b.TeamName);
}
internal sealed record StoredSnapshot(string TemplateKey, TemplateBlock[] AnswerSheet, TemplateBlock[] TaskSheet,
    Dictionary<string, TemplateValue> Parameters, StoredBlank[] Blanks, PrintableTask[] Tasks)
{
    public static string Serialize(PrintSnapshot snapshot) => JsonSerializer.Serialize(new StoredSnapshot(snapshot.Template.Key,
        snapshot.Template.AnswerSheet.ToArray(), snapshot.Template.TaskSheet.ToArray(), new(snapshot.Parameters),
        snapshot.Blanks.Select(StoredBlank.From).ToArray(), snapshot.Tasks.ToArray()));
    public static PrintSnapshot Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<StoredSnapshot>(json) ?? throw new InvalidDataException("Missing print snapshot.");
        return new(new(document.TemplateKey, document.AnswerSheet, document.TaskSheet), document.Parameters, document.Blanks.Select(b => b.Restore()), document.Tasks);
    }
}
