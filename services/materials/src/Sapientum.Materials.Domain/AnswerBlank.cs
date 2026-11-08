namespace Sapientum.Materials.Domain;

public enum ModifierKind { Add, Multiply }

public sealed record ScoreModifier
{
    public ModifierKind Kind { get; }
    public decimal Value { get; }
    public ScoreModifier(ModifierKind kind, decimal value)
    {
        if (!Enum.IsDefined(kind)) throw new DomainException("VALIDATION_ERROR", "Unknown modifier.");
        Kind = kind; Value = value;
    }
    public string Label => Kind == ModifierKind.Add ? $"+{Value}" : $"×{Value}";
}

/// <summary>Immutable issued identity. Reprints never create or change it.</summary>
public sealed class AnswerBlank
{
    public string Id { get; }
    public long Number { get; }
    public string GameId { get; }
    public string TeamId { get; }
    public string TaskId { get; }
    public string TaskNumber { get; }
    public string QrToken { get; }
    public ScoreModifier? Modifier { get; }

    public AnswerBlank(string id, long number, string gameId, string teamId, string taskId,
        string taskNumber, string qrToken, ScoreModifier? modifier)
    {
        Id = Guard.Id(id, nameof(id));
        if (number < 1) throw new DomainException("VALIDATION_ERROR", "Blank number must be positive.");
        Number = number; GameId = Guard.Id(gameId, nameof(gameId)); TeamId = Guard.Id(teamId, nameof(teamId));
        TaskId = Guard.Id(taskId, nameof(taskId)); TaskNumber = Guard.Id(taskNumber, nameof(taskNumber));
        QrToken = Guard.Id(qrToken, nameof(qrToken)); Modifier = modifier;
    }

    public void EnsurePrintableIn(string gameId, string? teamId)
    {
        if (GameId != gameId || (teamId is not null && TeamId != teamId))
            throw new DomainException("MATERIAL_SCOPE_MISMATCH", "The blank is outside the selected material scope.");
    }
}

public sealed record BlankIdentity(string Id, long Number, string GameId, string TeamId,
    string TaskId, string TaskNumber, ScoreModifier? Modifier);
