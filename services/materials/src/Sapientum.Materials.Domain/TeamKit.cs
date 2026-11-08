namespace Sapientum.Materials.Domain;

public sealed class TeamKit
{
    public string Id { get; }
    public string GameId { get; }
    public string TeamId { get; }
    public int AnswerBlankCount { get; }
    public int TaskCardCount { get; }
    public bool TaskCardsShared { get; }
    public string TemplateKey { get; }
    public IReadOnlyList<string> BlankIds { get; }

    public TeamKit(string id, string gameId, string teamId, int answerBlankCount, int taskCardCount,
        bool taskCardsShared, string templateKey, IEnumerable<string> blankIds)
    {
        Id = Guard.Id(id, nameof(id)); GameId = Guard.Id(gameId, nameof(gameId)); TeamId = Guard.Id(teamId, nameof(teamId));
        TemplateKey = Guard.Id(templateKey, nameof(templateKey));
        if (answerBlankCount < 0 || taskCardCount < 0) throw new DomainException("VALIDATION_ERROR", "Invalid material counts.");
        BlankIds = Array.AsReadOnly(blankIds.ToArray());
        if (BlankIds.Count != answerBlankCount || BlankIds.Distinct().Count() != BlankIds.Count)
            throw new DomainException("VALIDATION_ERROR", "A kit must contain the configured number of distinct blanks.");
        AnswerBlankCount = answerBlankCount; TaskCardCount = taskCardCount; TaskCardsShared = taskCardsShared;
    }

    public static TeamKit Prepare(string id, GameMaterials game, TeamAdmission team, IEnumerable<string> blankIds)
    {
        team.EnsureAdmittedTo(game.Id);
        return new(id, game.Id, team.Id, game.AnswerBlankCount, game.TaskCardCount, game.TaskCardsShared, game.TemplateKey, blankIds);
    }
}
