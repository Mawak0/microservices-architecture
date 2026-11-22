using Olympiad.Review.Domain;

namespace Olympiad.Review.Application;

public sealed record AppealAvailabilityView(bool Allowed, int Remaining, string? Code = null);

public sealed class AppealAvailability(ITransactions transactions, IGameProfiles games, IClock clock, AppealEligibility eligibility)
{
    public async Task<AppealAvailabilityView> Get(string id, CancellationToken ct)
    {
        var gameId = (await transactions.Run<string>(null, s => new(s.Check(id).Receipt.GameId), ct)).Value;
        var game = await games.Get(gameId, ct);
        var now = await clock.Now(ct);
        return (await transactions.Run<AppealAvailabilityView>(null, s =>
        {
            var check = s.Check(id);
            var remaining = s.Quotas.Get(gameId).Remaining;
            try { eligibility.Require(game, check, now); }
            catch (DomainFailure failure) { return new(new(false, remaining, failure.Code)); }

            if (s.Appeals.Exists(gameId, check.Receipt.TeamId, check.Receipt.TaskId))
                return new(new(false, remaining, "APPEAL_ALREADY_USED"));
            if (check.Stage != CheckStage.Checked || check.Verdict is null)
                return new(new(false, remaining, "CHECK_NOT_REVIEWED"));
            return new(remaining > 0 ? new(true, remaining) : new(false, 0, "APPEAL_QUOTA_EXHAUSTED"));
        }, ct)).Value;
    }
}
