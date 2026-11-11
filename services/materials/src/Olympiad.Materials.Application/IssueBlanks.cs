using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Application;

public sealed record IssueBlanksCommand(string GameId, string TeamId, string TaskId, int Count, ScoreModifier? Modifier);

public sealed class IssueBlanks(IGameMaterialsGateway games, ITeamAdmissionGateway teams,
    IMaterialsTransactions transactions, IBlankTokenProtector tokens, IClock clock, int maximumBatchSize = 10000)
{
    public async Task<IReadOnlyList<AnswerBlank>> ExecuteAsync(CommandContext context, IssueBlanksCommand command, CancellationToken cancellationToken)
    {
        context.Actor.Require(MaterialPermission.Issue);
        if (maximumBatchSize < 1) throw new InvalidOperationException("The configured maximum batch size must be positive.");
        if (command.Count < 1 || command.Count > maximumBatchSize) throw new DomainException("VALIDATION_ERROR", $"Count must be between 1 and the configured batch limit ({maximumBatchSize}).");
        var scope = CommandReplay.Scope(context, "issue-blanks");
        var fingerprint = CommandReplay.Fingerprint(command.GameId, command.TeamId, command.TaskId, command.Count.ToString(),
            command.Modifier?.Kind.ToString(), command.Modifier?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var replay = await transactions.ExecuteAsync(async (session, ct) =>
        {
            var prior = await CommandReplay.FindAsync(session, scope, context.IdempotencyKey, fingerprint, ct);
            return prior is null ? null : await ReadReplayAsync(session, prior, ct);
        }, cancellationToken);
        if (replay is not null) return replay;
        var game = await games.GetAsync(command.GameId, cancellationToken);
        var team = await teams.GetAsync(command.TeamId, cancellationToken); team.EnsureAdmittedTo(game.Id);
        var assignment = game.RequireIssuableTask(command.TaskId, command.Modifier);
        var now = await clock.UtcNowAsync(cancellationToken);
        return await transactions.ExecuteAsync<IReadOnlyList<AnswerBlank>>(async (session, ct) =>
        {
            var prior = await CommandReplay.FindAsync(session, scope, context.IdempotencyKey, fingerprint, ct);
            if (prior is not null)
            {
                return await ReadReplayAsync(session, prior, ct);
            }
            var blanks = await new BlankIssuance(tokens).CreateAsync(session, game, team.Id,
                Enumerable.Repeat(assignment, command.Count).ToArray(), command.Modifier, ct);
            var ids = blanks.Select(b => b.Id).ToArray();
            await session.Idempotency.AddAsync(new(scope, context.IdempotencyKey, fingerprint, ids), ct);
            await session.Outbox.AddAsync(new(Guid.NewGuid().ToString(), "BlanksIssued", now, ids[0], game.Id, team.Id, BlankIds: ids), ct);
            return blanks;
        }, cancellationToken);
    }
    private static async Task<IReadOnlyList<AnswerBlank>> ReadReplayAsync(IMaterialsSession session, IdempotencyRecord prior, CancellationToken cancellationToken)
    {
        var existing = new List<AnswerBlank>();
        foreach (var id in prior.ResourceIds) existing.Add(await session.Blanks.FindAsync(id, cancellationToken) ?? throw new InvalidOperationException("Persisted replay points to a missing blank."));
        return existing.AsReadOnly();
    }
}
