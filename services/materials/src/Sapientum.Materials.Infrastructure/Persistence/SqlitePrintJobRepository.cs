using Microsoft.Data.Sqlite;
using Sapientum.Materials.Application;
using Sapientum.Materials.Domain;
using System.Globalization;

namespace Sapientum.Materials.Infrastructure.Persistence;

internal sealed class SqlitePrintJobRepository(SqliteConnection connection, SqliteTransaction transaction)
    : SqliteRepository(connection, transaction), IPrintJobRepository
{
    public async Task AddAsync(PrintJob job, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            INSERT INTO print_jobs(id,game_id,team_id,snapshot_json,status,created_order)
            VALUES($id,$game,$team,$snapshot,$status,(SELECT COALESCE(MAX(created_order),0)+1 FROM print_jobs))
            """, ("$id", job.Id), ("$game", job.GameId), ("$team", job.TeamId), ("$snapshot", StoredSnapshot.Serialize(job.Snapshot)), ("$status", job.Status.ToString()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public Task<PrintJob?> FindAsync(string id, CancellationToken cancellationToken) => FindByAsync("id=$id", [("$id", id)], cancellationToken);
    public Task<PrintJob?> FindNextPendingAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        FindByAsync("status='Pending' OR (status='Running' AND processing_expires_at<=$now) ORDER BY created_order LIMIT 1", [("$now", now.ToString("O"))], cancellationToken);
    private async Task<PrintJob?> FindByAsync(string where, (string, object?)[] parameters, CancellationToken cancellationToken)
    {
        await using var command = Command($"SELECT id,game_id,team_id,snapshot_json,status,artifact_url,error_code,error_message,processing_token,processing_expires_at FROM print_jobs WHERE {where}", parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var failure = reader.IsDBNull(6) ? null : new PrintFailure(reader.GetString(6), reader.GetString(7));
        var expiry = reader.IsDBNull(9) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture);
        return new(reader.GetString(0), reader.GetString(1), NullableText(reader, 2), StoredSnapshot.Deserialize(reader.GetString(3)),
            Enum.Parse<PrintStatus>(reader.GetString(4)), NullableText(reader, 5), failure, NullableText(reader, 8), expiry);
    }
    public async Task SaveAsync(PrintJob job, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            UPDATE print_jobs SET status=$status,artifact_url=$artifact,error_code=$code,error_message=$message,
            processing_token=$token,processing_expires_at=$expiry WHERE id=$id
            """, ("$id", job.Id), ("$status", job.Status.ToString()), ("$artifact", job.ArtifactUrl), ("$code", job.Error?.Code),
            ("$message", job.Error?.Message), ("$token", job.ProcessingToken), ("$expiry", job.ProcessingExpiresAt?.ToString("O")));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task StoreArtifactAsync(string jobId, byte[] artifact, CancellationToken cancellationToken)
    {
        await using var command = Command("INSERT INTO pdf_artifacts VALUES($id,$bytes) ON CONFLICT(print_job_id) DO UPDATE SET content=$bytes", ("$id", jobId), ("$bytes", artifact));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task<byte[]?> FindArtifactAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT content FROM pdf_artifacts WHERE print_job_id=$id", ("$id", jobId));
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }
}
