using System.Text.Json;
using Microsoft.Data.Sqlite;
using Olympiad.Review.Application;
using Olympiad.Review.Domain;
namespace Olympiad.Review.Infrastructure;
public sealed class SqliteTransactions(SqliteDatabase database) : ITransactions
{
    public Task<Outcome<T>?> Replay<T>(CommandIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var scope = new DatabaseScope(connection, transaction, cancellationToken);
        return Task.FromResult(Read<T>(scope, identity));
    }
    public Task<Outcome<T>> Run<T>(CommandIdentity? identity,
        Func<IReviewSession, Outcome<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.Open();
        // SQLite's write reservation provides cross-process consistency; there is no process-wide lock.
        using var transaction = connection.BeginTransaction(deferred: false);
        var scope = new DatabaseScope(connection, transaction, cancellationToken);
        if (identity is not null)
        {
            var replay = Read<T>(scope, identity);
            if (replay is not null) { transaction.Commit(); return Task.FromResult(replay); }
        }
        var result = action(new ReviewSession(scope));
        if (identity is not null) scope.Execute("""
            INSERT INTO idempotency(actor_id,operation,key,fingerprint,result)
            VALUES($actor,$operation,$key,$fingerprint,$result)
            """, ("$actor", identity.ActorId), ("$operation", identity.Operation), ("$key", identity.Key),
            ("$fingerprint", identity.Fingerprint), ("$result", DatabaseScope.Serialize(result)));
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return Task.FromResult(result);
    }
    private static Outcome<T>? Read<T>(DatabaseScope scope, CommandIdentity identity)
    {
        using var command = scope.Command("""
            SELECT fingerprint,result FROM idempotency WHERE actor_id=$actor AND operation=$operation AND key=$key
            """, ("$actor", identity.ActorId), ("$operation", identity.Operation), ("$key", identity.Key));
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
