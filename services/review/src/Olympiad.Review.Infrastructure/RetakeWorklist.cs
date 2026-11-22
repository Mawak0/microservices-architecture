using Olympiad.Review.Application;
using Olympiad.Review.Domain;

namespace Olympiad.Review.Infrastructure;

public sealed class RetakeWorklist(PostgresDatabase database) : IRetakeWorklist
{
    public Task<IReadOnlyList<RetakeTicket>> List(string gameId, int offset, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RetakeTicket>>(database.Store.Run((connection, transaction) =>
            new DatabaseScope(connection, transaction, ct).ReadMany<CheckSnapshot>("""
                SELECT snapshot FROM checks WHERE game_id=@game AND queue_stage=@stage
                ORDER BY arrival_order OFFSET @offset LIMIT @limit
                """, ("@game", gameId), ("@stage", (int)CheckStage.AwaitingRetake), ("@offset", offset), ("@limit", limit))
                .Select(snapshot => RetakeTicket.From(AnswerCheck.Restore(snapshot))!).ToArray(), ct));
}
