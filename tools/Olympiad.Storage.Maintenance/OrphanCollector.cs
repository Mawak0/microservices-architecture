using Npgsql;
using Olympiad.Persistence.Postgres;
using Olympiad.Storage.S3;

namespace Olympiad.Storage.Maintenance;

/// <summary>Offline reconciliation. The caller must stop every replica before applying a plan.</summary>
public sealed class OrphanCollector(PostgresStore database,S3Objects objects,string service)
{
    public async Task<IReadOnlyList<string>> ReconcileAsync(DateTimeOffset cutoff,bool apply,CancellationToken ct)
    {
        if(service is not ("materials" or "intake")) throw new ArgumentException("Select materials or intake.");
        await using var connection=await database.OpenAsync(ct);
        if(apply)
        {
            await using var active=new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname=current_database() AND usename=current_user AND pid<>pg_backend_pid()",connection);
            for(var attempt=0;;attempt++)
            {
                if((long)(await active.ExecuteScalarAsync(ct))! == 0) break;
                if(attempt==20) throw new InvalidOperationException("Stop all service replicas and other connections before deleting objects.");
                // A just-closed TCP session can briefly remain visible while its backend exits.
                await Task.Delay(50,ct);
            }
        }
        var references=new HashSet<string>(StringComparer.Ordinal);
        var protectedPrefixes=new List<string>();
        await using(var keys=new NpgsqlCommand(service=="materials"?"SELECT object_key FROM pdf_artifacts":"SELECT object_key FROM receipts",connection))
        await using(var reader=await keys.ExecuteReaderAsync(ct))
            while(await reader.ReadAsync(ct)) references.Add(reader.GetString(0));
        if(service=="materials")
        {
            await using var pending=new NpgsqlCommand("SELECT id,processing_token FROM print_jobs WHERE status='Running' AND processing_token IS NOT NULL",connection);
            await using var reader=await pending.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct)) protectedPrefixes.Add(S3Objects.Key(reader.GetString(0)+":"+reader.GetString(1),""));
        }
        else
        {
            await using var pending=new NpgsqlCommand("SELECT receipt_id,verified_image FROM submissions",connection);
            await using var reader=await pending.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct)) references.Add(S3Objects.Key(reader.GetString(0),S3Objects.Digest(reader.GetFieldValue<byte[]>(1))));
        }
        var candidates=(await objects.ListOlderThanAsync(cutoff,ct)).Where(key=>!references.Contains(key)&&!protectedPrefixes.Any(prefix=>key.StartsWith(prefix,StringComparison.Ordinal))).ToArray();
        if(apply) foreach(var key in candidates) await objects.DeleteAsync(key,ct);
        return candidates;
    }
}
