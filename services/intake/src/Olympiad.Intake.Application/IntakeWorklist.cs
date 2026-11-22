using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Application;

public sealed record ReceiptSearch(string GameId, string? TeamId, string? TaskId, string? BlankId,
    string? Id, int Offset, int Limit);
public sealed record ReceiptListItem(string Id, string BlankId, string GameId, string TeamId,
    string TaskId, string TaskNumber, DateTimeOffset AcceptedAt);

public interface IReceiptWorklistReader
{
    Task<IReadOnlyList<ReceiptListItem>> SearchAsync(ReceiptSearch search, CancellationToken ct);
}

public sealed class PreviewBlank(IIntakeUnitOfWork store, IBlankRegistry registry, IAdmissionGateway games,
    ITeamAdmissionGateway teams, IClock clock)
{
    public async Task<BlankBinding> ExecuteAsync(string gameId, string qrToken, CancellationToken ct)
    {
        var blank = await registry.VerifyAsync(qrToken, ct);
        blank.RequireValid();
        RequireSameGame(gameId, blank);
        var admission = await games.GetAsync(gameId, ct);
        (await teams.GetAsync(blank.TeamId, ct)).RequireEligible(blank);
        var now = await clock.NowAsync(ct);
        admission.RequireOpen(now);
        return await store.ExecuteAsync(async (tx, token) =>
        {
            var session = await tx.Sessions.FindAsync(gameId, token)
                ?? throw new IntakeException(IntakeError.IntakeClosed, "Intake has not been opened.");
            session.RequireAccepting(admission, now);
            if (await tx.Receipts.ExistsForBlankAsync(blank.BlankId, token) ||
                await tx.Submissions.ExistsForBlankAsync(blank.BlankId, token))
                throw new IntakeException(IntakeError.BlankAlreadyAccepted, "The blank is already accepted or reserved.");
            return blank;
        }, ct);
    }

    internal static void RequireSameGame(string? gameId, BlankBinding blank)
    {
        if (gameId is not null && gameId != blank.GameId)
            throw new IntakeException(IntakeError.InvalidQr, "The blank belongs to a different game.");
    }
}
