using System.Collections.ObjectModel;

namespace Sapientum.Materials.Domain;

public sealed record TaskAssignment(string TaskId, string TaskNumber);

/// <summary>Capabilities and immutable task assignments, independent of a game's name.</summary>
public sealed class GameMaterials
{
    public string Id { get; }
    public bool ModifiersAllowed { get; }
    public int AnswerBlankCount { get; }
    public int TaskCardCount { get; }
    public bool TaskCardsShared { get; }
    public string TemplateKey { get; }
    public IReadOnlyList<TaskAssignment> Tasks { get; }
    public IReadOnlyDictionary<string, TemplateValue> TemplateParameters { get; }

    public GameMaterials(string id, bool modifiersAllowed, int answerBlankCount, int taskCardCount,
        bool taskCardsShared, string templateKey, IEnumerable<TaskAssignment> tasks,
        IReadOnlyDictionary<string, TemplateValue> templateParameters)
    {
        Id = Guard.Id(id, nameof(id)); TemplateKey = Guard.Id(templateKey, nameof(templateKey));
        if (answerBlankCount < 0 || taskCardCount < 0) throw new DomainException("VALIDATION_ERROR", "Material counts cannot be negative.");
        ModifiersAllowed = modifiersAllowed; AnswerBlankCount = answerBlankCount; TaskCardCount = taskCardCount;
        TaskCardsShared = taskCardsShared; Tasks = Array.AsReadOnly(tasks.ToArray());
        if (Tasks.Select(t => t.TaskId).Distinct().Count() != Tasks.Count)
            throw new DomainException("VALIDATION_ERROR", "Task assignments must be unique.");
        TemplateParameters = new ReadOnlyDictionary<string, TemplateValue>(new Dictionary<string, TemplateValue>(templateParameters));
    }

    public TaskAssignment RequireIssuableTask(string taskId, ScoreModifier? modifier)
    {
        if (modifier is not null && !ModifiersAllowed)
            throw new DomainException("MODIFIER_NOT_ALLOWED", "This game does not permit score modifiers.");
        return Tasks.SingleOrDefault(t => t.TaskId == taskId)
            ?? throw new DomainException("TASK_NOT_IN_GAME", "The task is not assigned to this game.");
    }

    public TaskAssignment RequirePrintableTask(string taskId) => Tasks.SingleOrDefault(t => t.TaskId == taskId)
        ?? throw new DomainException("MATERIAL_SCOPE_MISMATCH", "The task is not assigned to the selected game.");
}

public sealed class TeamAdmission(string id, string gameId, bool admitted, string name)
{
    public string Id { get; } = Guard.Id(id, nameof(id));
    public string GameId { get; } = Guard.Id(gameId, nameof(gameId));
    public string Name { get; } = name;
    public void EnsureAdmittedTo(string targetGame)
    {
        if (!admitted || GameId != targetGame)
            throw new DomainException("TEAM_NOT_ADMITTED", "The team is not admitted to this game.");
    }
}
