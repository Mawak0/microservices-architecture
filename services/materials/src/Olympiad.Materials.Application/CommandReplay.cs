using System.Security.Cryptography;
using System.Text;
using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Application;

internal static class CommandReplay
{
    public static string Scope(CommandContext context, string operation)
    {
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey)) throw new DomainException("VALIDATION_ERROR", "Idempotency-Key is required.");
        return $"{context.Actor.Id}:{operation}";
    }
    public static string Fingerprint(params string?[] parts)
    {
        var framed = string.Concat(parts.Select(p => p is null ? "-1:" : $"{p.Length}:{p}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(framed)));
    }
    public static async Task<IdempotencyRecord?> FindAsync(IMaterialsSession session, string scope, string key,
        string fingerprint, CancellationToken cancellationToken)
    {
        var prior = await session.Idempotency.FindAsync(scope, key, cancellationToken);
        if (prior is not null && prior.Fingerprint != fingerprint)
            throw new DomainException("IDEMPOTENCY_KEY_REUSED", "The key was previously used with a different request.");
        return prior;
    }
}

internal sealed class BlankIssuance(IBlankTokenProtector tokens)
{
    public async Task<IReadOnlyList<AnswerBlank>> CreateAsync(IMaterialsSession session, GameMaterials game,
        string teamId, IReadOnlyList<TaskAssignment> assignments, ScoreModifier? modifier, CancellationToken cancellationToken)
    {
        var first = await session.Blanks.ReserveNumbersAsync(assignments.Count, cancellationToken);
        var blanks = new List<AnswerBlank>();
        for (var i = 0; i < assignments.Count; i++)
        {
            var assignment = assignments[i];
            var identity = new BlankIdentity(Guid.NewGuid().ToString(), first + i, game.Id, teamId, assignment.TaskId, assignment.TaskNumber, modifier);
            var blank = new AnswerBlank(identity.Id, identity.Number, identity.GameId, identity.TeamId,
                identity.TaskId, identity.TaskNumber, tokens.Protect(identity), modifier);
            await session.Blanks.AddAsync(blank, cancellationToken); blanks.Add(blank);
        }
        return blanks.AsReadOnly();
    }
}
