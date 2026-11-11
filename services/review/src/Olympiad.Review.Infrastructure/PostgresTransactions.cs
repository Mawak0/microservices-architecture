using System.Text.Json;
using Npgsql;
using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Infrastructure;
public sealed class NpgsqlTransactions(PostgresDatabase database) : ITransactions
{
    public Task<Outcome<T>?> Replay<T>(CommandIdentity identity, CancellationToken ct) =>
        Task.FromResult(database.Store.Run((connection, transaction) => Read<T>(new DatabaseScope(connection, transaction, ct), identity), ct));
    public Task<Outcome<T>> Run<T>(CommandIdentity? identity, Func<IReviewSession, Outcome<T>> action, CancellationToken ct) =>
        Task.FromResult(database.Store.Run((connection, transaction) =>
        {
            var scope = new DatabaseScope(connection, transaction, ct);
            if (identity is not null && Read<T>(scope, identity) is { } replay) return replay;
            var result = action(new ReviewSession(scope));
            if (identity is not null) scope.Execute("INSERT INTO idempotency(actor_id,operation,key,fingerprint,result) VALUES(@actor,@operation,@key,@fingerprint,@result)",
                ("@actor", identity.ActorId), ("@operation", identity.Operation), ("@key", identity.Key),
                ("@fingerprint", identity.Fingerprint), ("@result", DatabaseScope.Serialize(result)));
            return result;
        }, ct));
    private static Outcome<T>? Read<T>(DatabaseScope scope, CommandIdentity identity)
    {
        using var command = scope.Command("""
            SELECT fingerprint,result FROM idempotency WHERE actor_id=@actor AND operation=@operation AND key=@key
            """, ("@actor", identity.ActorId), ("@operation", identity.Operation), ("@key", identity.Key));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != identity.Fingerprint) throw new DomainFailure("IDEMPOTENCY_KEY_REUSED");
        return JsonSerializer.Deserialize<Outcome<T>>(reader.GetString(1), DatabaseScope.Json)!;
    }
}
internal sealed class ReviewSession : IReviewSession
{
    public ICheckRepository Checks { get; }
    public IAppealRepository Appeals { get; }
    public IQuotaRepository Quotas { get; }
    public ICompletionRepository Completions { get; }
    public IIncomingEvents Inbox { get; }
    public IEventOutbox Events { get; }
    public ReviewSession(DatabaseScope scope)
    {
        Checks = new CheckRepository(scope); Appeals = new AppealRepository(scope);
        Quotas = new QuotaRepository(scope); Completions = new CompletionRepository(scope);
        Inbox = new IncomingEvents(scope); Events = new EventOutbox(scope);
    }
}
