using Npgsql;
using Olympiad.Intake.Application;

namespace Olympiad.Intake.Infrastructure.Persistence;

internal sealed class SubmissionRepository(PostgresScope db) : ISubmissionRepository
{
    private const string Columns = "receipt_id,blank_id,game_id,team_id,task_id,task_number,verified_image,media_type,scope,operation_key,fingerprint,worker_token,lease_until";
    public Task<bool> ExistsForBlankAsync(string blankId, CancellationToken ct) => db.ExistsAsync("SELECT 1 FROM submissions WHERE blank_id=@id", ct, ("@id", blankId));
    public async Task<PendingSubmission?> FindForOperationAsync(OperationIdentity identity, CancellationToken ct)
    {
        await using var command = db.Command($"SELECT {Columns} FROM submissions WHERE scope=@scope AND operation_key=@key", ("@scope", identity.Scope), ("@key", identity.Key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }
    public Task AddAsync(PendingSubmission pending, CancellationToken ct) => db.ExecuteAsync($"""
        INSERT INTO submissions({Columns}) VALUES(@id,@blank,@game,@team,@task,@number,@image,@media,@scope,@key,@fingerprint,@worker,@lease)
        """, ct, ("@id", pending.ReceiptId), ("@blank", pending.Blank.BlankId), ("@game", pending.Blank.GameId),
        ("@team", pending.Blank.TeamId), ("@task", pending.Blank.TaskId), ("@number", pending.Blank.TaskNumber),
        ("@image", pending.Image.ToArray()), ("@media", pending.MediaType), ("@scope", pending.Operation.Scope),
        ("@key", pending.Operation.Key), ("@fingerprint", pending.Operation.Fingerprint),
        ("@worker", pending.WorkerToken), ("@lease", PostgresScope.Timestamp(pending.LeaseUntil)));
    public Task<bool> IsOwnedAsync(string receiptId, string workerToken, CancellationToken ct) => db.ExistsAsync(
        "SELECT 1 FROM submissions WHERE receipt_id=@id AND worker_token=@worker", ct, ("@id", receiptId), ("@worker", workerToken));
    public Task RemoveAsync(string receiptId, string workerToken, CancellationToken ct) => db.ExecuteAsync(
        "DELETE FROM submissions WHERE receipt_id=@id AND worker_token=@worker", ct, ("@id", receiptId), ("@worker", workerToken));
    public async Task<PendingSubmission?> ClaimExpiredAsync(string workerToken, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct)
    {
        PendingSubmission? pending;
        await using (var command = db.Command($"SELECT {Columns} FROM submissions WHERE lease_until<=@now ORDER BY lease_until LIMIT 1", ("@now", PostgresScope.Timestamp(now))))
        await using (var reader = await command.ExecuteReaderAsync(ct)) pending = await reader.ReadAsync(ct) ? Read(reader) : null;
        if (pending is null) return null;
        await db.ExecuteAsync("UPDATE submissions SET worker_token=@worker,lease_until=@lease WHERE receipt_id=@id", ct,
            ("@worker", workerToken), ("@lease", PostgresScope.Timestamp(leaseUntil)), ("@id", pending.ReceiptId));
        return pending with { WorkerToken = workerToken, LeaseUntil = leaseUntil };
    }
    private static PendingSubmission Read(NpgsqlDataReader reader) => new(reader.GetString(0),
        new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)),
        reader.GetFieldValue<byte[]>(6), reader.GetString(7), new(reader.GetString(8), reader.GetString(9), reader.GetString(10)),
        reader.GetString(11), DateTimeOffset.Parse(reader.GetString(12)));
}
