using Sapientum.Materials.Domain;

namespace Sapientum.Materials.Application;

public sealed record PrepareKitCommand(string GameId, string TeamId);
public sealed class PrepareKit(IGameMaterialsGateway games, ITeamAdmissionGateway teams,
    IMaterialsTransactions transactions, IBlankTokenProtector tokens, IClock clock)
{
    public async Task<TeamKit> ExecuteAsync(CommandContext context, PrepareKitCommand command, CancellationToken cancellationToken)
    {
        context.Actor.Require(MaterialPermission.Prepare);
        var scope = CommandReplay.Scope(context, "prepare-kit");
        var fingerprint = CommandReplay.Fingerprint(command.GameId, command.TeamId);
        var replay = await transactions.ExecuteAsync(async (session, ct) =>
        {
            var prior = await CommandReplay.FindAsync(session, scope, context.IdempotencyKey, fingerprint, ct);
            return prior is null ? null : await session.Kits.FindAsync(prior.ResourceIds.Single(), ct)
                ?? throw new InvalidOperationException("Persisted replay points to a missing kit.");
        }, cancellationToken);
        if (replay is not null) return replay;
        var game = await games.GetAsync(command.GameId, cancellationToken);
        var team = await teams.GetAsync(command.TeamId, cancellationToken); team.EnsureAdmittedTo(game.Id);
        if (game.AnswerBlankCount > 0 && game.Tasks.Count == 0) throw new DomainException("TASK_NOT_IN_GAME", "A kit cannot be issued without assigned tasks.");
        var now = await clock.UtcNowAsync(cancellationToken);
        return await transactions.ExecuteAsync(async (session, ct) =>
        {
            var prior = await CommandReplay.FindAsync(session, scope, context.IdempotencyKey, fingerprint, ct);
            if (prior is not null) return await session.Kits.FindAsync(prior.ResourceIds.Single(), ct)
                ?? throw new InvalidOperationException("Persisted replay points to a missing kit.");
            var kit = await session.Kits.FindForTeamAsync(game.Id, team.Id, ct);
            if (kit is null)
            {
                var assignments = Enumerable.Range(0, game.AnswerBlankCount).Select(i => game.Tasks[i % game.Tasks.Count]).ToArray();
                var blanks = assignments.Length == 0 ? [] : await new BlankIssuance(tokens).CreateAsync(session, game, team.Id, assignments, null, ct);
                kit = TeamKit.Prepare(Guid.NewGuid().ToString(), game, team, blanks.Select(b => b.Id));
                await session.Kits.AddAsync(kit, ct);
                await session.Outbox.AddAsync(new(Guid.NewGuid().ToString(), "KitPrepared", now, kit.Id, game.Id, team.Id, KitId: kit.Id), ct);
            }
            await session.Idempotency.AddAsync(new(scope, context.IdempotencyKey, fingerprint, [kit.Id]), ct);
            return kit;
        }, cancellationToken);
    }
}
