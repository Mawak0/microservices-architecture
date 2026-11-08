using Sapientum.Materials.Domain;

namespace Sapientum.Materials.Application;

public sealed record CreatePrintJobCommand(string GameId, string? TeamId, IReadOnlyList<string> BlankIds, IReadOnlyList<string> TaskIds);
public sealed class CreatePrintJob(IGameMaterialsGateway games, ITeamAdmissionGateway teams,
    ITaskStatementGateway tasks, ITemplateRepository templates, IMaterialsTransactions transactions)
{
    public async Task<PrintJob> ExecuteAsync(CommandContext context, CreatePrintJobCommand command, CancellationToken cancellationToken)
    {
        context.Actor.Require(MaterialPermission.Print);
        if (command.BlankIds.Count + command.TaskIds.Count == 0 || command.BlankIds.Distinct().Count() != command.BlankIds.Count ||
            command.TaskIds.Distinct().Count() != command.TaskIds.Count)
            throw new DomainException("VALIDATION_ERROR", "Select distinct materials, at least one item.");
        if (command.TeamId is null) context.Actor.Require(MaterialPermission.Issue);
        var scope = CommandReplay.Scope(context, "create-print-job");
        var fingerprint = CommandReplay.Fingerprint(command.GameId, command.TeamId,
            CommandReplay.Fingerprint(command.BlankIds.ToArray()), CommandReplay.Fingerprint(command.TaskIds.ToArray()));
        var replay = await transactions.ExecuteAsync(async (session, ct) =>
        {
            var prior = await CommandReplay.FindAsync(session, scope, context.IdempotencyKey, fingerprint, ct);
            return prior is null ? null : await RestoreSubmissionAsync(session, prior, ct);
        }, cancellationToken);
        if (replay is not null) return replay;
        var game = await games.GetAsync(command.GameId, cancellationToken);
        if (command.TeamId is not null) (await teams.GetAsync(command.TeamId, cancellationToken)).EnsureAdmittedTo(game.Id);
        var blanks = await transactions.ExecuteAsync(async (session, ct) =>
        {
            var list = new List<AnswerBlank>();
            foreach (var id in command.BlankIds)
            {
                var blank = await session.Blanks.FindAsync(id, ct) ?? throw new DomainException("MATERIAL_SCOPE_MISMATCH", "A selected blank does not exist.");
                blank.EnsurePrintableIn(game.Id, command.TeamId); list.Add(blank);
            }
            return list;
        }, cancellationToken);
        var printableTasks = new List<PrintableTask>();
        foreach (var id in command.TaskIds)
        {
            var assigned = game.RequirePrintableTask(id);
            printableTasks.Add(new(id, assigned.TaskNumber, await tasks.GetStatementAsync(id, cancellationToken)));
        }
        var teamNames = new Dictionary<string, string>();
        foreach (var teamId in blanks.Select(b => b.TeamId).Distinct())
            teamNames[teamId] = (await teams.GetAsync(teamId, cancellationToken)).Name;
        var template = await templates.GetAsync(game.TemplateKey, cancellationToken);
        var snapshot = new PrintSnapshot(template, game.TemplateParameters, blanks.Select(b => new PrintableBlank(b, teamNames[b.TeamId])), printableTasks);
        return await transactions.ExecuteAsync(async (session, ct) =>
        {
            var prior = await CommandReplay.FindAsync(session, scope, context.IdempotencyKey, fingerprint, ct);
            if (prior is not null)
            {
                return await RestoreSubmissionAsync(session, prior, ct);
            }
            var job = new PrintJob(Guid.NewGuid().ToString(), game.Id, command.TeamId, snapshot);
            await session.PrintJobs.AddAsync(job, ct);
            await session.Idempotency.AddAsync(new(scope, context.IdempotencyKey, fingerprint, [job.Id]), ct);
            return job;
        }, cancellationToken);
    }
    private static async Task<PrintJob> RestoreSubmissionAsync(IMaterialsSession session, IdempotencyRecord prior, CancellationToken cancellationToken)
    {
        var original = await session.PrintJobs.FindAsync(prior.ResourceIds.Single(), cancellationToken) ?? throw new InvalidOperationException("Missing replay print job.");
        return new(original.Id, original.GameId, original.TeamId, original.Snapshot);
    }
}
