namespace Sapientum.Review.Domain;
public sealed record CompletionSnapshot(string GameId, int AdmissionVersion,
    IReadOnlyList<string>? ReceiptIds, bool Published);
public sealed record CompletionAssessment(string GameId, int AdmissionVersion, bool ManifestReceived,
    bool Complete, IReadOnlyList<string> MissingReceiptIds, IReadOnlyList<string> PendingCheckIds,
    IReadOnlyList<string> PendingAppealIds, bool AcceptingSubmissions);
public sealed class GameReviewCompletion
{
    private CompletionSnapshot state;
    public GameReviewCompletion(string gameId) => state = new(Required.Text(gameId), 1, null, false);
    private GameReviewCompletion(CompletionSnapshot state) => this.state = state with { ReceiptIds = state.ReceiptIds?.ToArray() };
    public static GameReviewCompletion Restore(CompletionSnapshot state) => new(state);
    public CompletionSnapshot Snapshot() => state with { ReceiptIds = state.ReceiptIds?.ToArray() };
    public void Synchronize(int version)
    {
        if (version > state.AdmissionVersion) state = new(state.GameId, version, null, false);
    }
    public void Receive(int version, IReadOnlyList<string> receipts, int currentVersion)
    {
        Synchronize(currentVersion);
        if (version > currentVersion) throw new DomainFailure("ADMISSION_VERSION_MISMATCH");
        if (version != currentVersion) return;
        foreach (var receipt in receipts) Required.Text(receipt);
        if (receipts.Count != receipts.Distinct().Count())
            throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
        var normalized = receipts.Order(StringComparer.Ordinal).ToArray();
        if (state.ReceiptIds is not null && !state.ReceiptIds.Order(StringComparer.Ordinal).SequenceEqual(normalized))
            throw new DomainFailure("MANIFEST_CONFLICT");
        state = state with { ReceiptIds = normalized };
    }
    public CompletionAssessment Assess(GameProfile game, DateTimeOffset now,
        IReadOnlyList<AnswerCheck> checks, IReadOnlyList<Appeal> appeals)
    {
        Synchronize(game.AdmissionVersion);
        var missing = state.ReceiptIds?.Except(checks.Select(c => c.Receipt.Id)).ToArray() ?? [];
        var pending = checks.Where(c => c.OriginalVerdict is null).Select(c => c.Id).ToArray();
        var pendingAppeals = appeals.Where(a => !a.Resolved).Select(a => a.Id).ToArray();
        var accepting = game.AcceptingSubmissions(now);
        var complete = state.AdmissionVersion == game.AdmissionVersion && state.ReceiptIds is not null && !accepting && missing.Length == 0 &&
            pending.Length == 0 && pendingAppeals.Length == 0 &&
            (!game.AppealsAllowed || now >= game.AppealDeadline) &&
            checks.All(c => state.ReceiptIds.Contains(c.Receipt.Id));
        return new(game.Id, game.AdmissionVersion, state.ReceiptIds is not null, complete,
            missing, pending, pendingAppeals, accepting);
    }
    public bool MarkPublished(CompletionAssessment assessment)
    {
        if (!assessment.Complete || state.Published) return false;
        state = state with { Published = true }; return true;
    }
}
