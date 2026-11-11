using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Application;

public sealed class MaterialQueries(IMaterialsTransactions transactions, IBlankTokenProtector tokens, IPrintArtifactStorage artifacts)
{
    public Task<TeamKit> KitAsync(Actor actor, string id, CancellationToken cancellationToken)
    {
        actor.Require(MaterialPermission.ReadKit);
        return transactions.ExecuteAsync(async (s, ct) => await s.Kits.FindAsync(id, ct) ?? throw new NotFoundException("Kit not found."), cancellationToken);
    }
    public Task<AnswerBlank> BlankAsync(Actor actor, string id, CancellationToken cancellationToken)
    {
        actor.Require(MaterialPermission.Read);
        return transactions.ExecuteAsync(async (s, ct) => await s.Blanks.FindAsync(id, ct) ?? throw new NotFoundException("Blank not found."), cancellationToken);
    }
    public Task<PrintJob> PrintJobAsync(Actor actor, string id, CancellationToken cancellationToken)
    {
        actor.Require(MaterialPermission.Print);
        return transactions.ExecuteAsync(async (s, ct) => await s.PrintJobs.FindAsync(id, ct) ?? throw new NotFoundException("Print job not found."), cancellationToken);
    }
    public async Task<byte[]> ArtifactAsync(Actor actor, string id, CancellationToken cancellationToken)
    {
        actor.Require(MaterialPermission.Print);
        var artifact = await transactions.ExecuteAsync(async (s, ct) => await s.PrintJobs.FindArtifactAsync(id, ct) ?? throw new NotFoundException("The PDF is not ready."), cancellationToken);
        return await artifacts.ReadAsync(artifact, cancellationToken);
    }
    public Task<AnswerBlank> VerifyAsync(Actor actor, string token, CancellationToken cancellationToken)
    {
        actor.Require(MaterialPermission.Verify);
        var identity = tokens.Unprotect(token) ?? throw new DomainException("INVALID_QR", "Unknown or forged QR.");
        return transactions.ExecuteAsync(async (s, ct) =>
        {
            var blank = await s.Blanks.FindByTokenAsync(token, ct);
            if (blank is null || blank.Id != identity.Id || blank.Number != identity.Number || blank.GameId != identity.GameId ||
                blank.TeamId != identity.TeamId || blank.TaskId != identity.TaskId || blank.TaskNumber != identity.TaskNumber || blank.Modifier != identity.Modifier)
                throw new DomainException("INVALID_QR", "The signed identity does not match the registry.");
            return blank;
        }, cancellationToken);
    }
}
