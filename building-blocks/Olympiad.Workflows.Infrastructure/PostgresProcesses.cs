using System.Text.Json;
using Npgsql;
using Olympiad.Persistence.Postgres;
using Olympiad.Workflows.Application;
using Olympiad.Workflows.Domain;

namespace Olympiad.Workflows.Infrastructure;

public sealed class ProcessKeyConflictException() : Exception("The idempotency key belongs to another request.");

public sealed class PostgresProcesses(PostgresStore database) : IProcessStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task InitializeAsync(CancellationToken ct) => database.InitializeAsync("""
        CREATE TABLE IF NOT EXISTS processes (
          id TEXT PRIMARY KEY, actor_id TEXT NOT NULL, scope TEXT NOT NULL, operation_key TEXT NOT NULL,
          fingerprint TEXT NOT NULL, snapshot TEXT NOT NULL, status TEXT NOT NULL,
          created_at TIMESTAMPTZ NOT NULL, next_attempt TIMESTAMPTZ NOT NULL, lease_until TIMESTAMPTZ,
          expected_checkpoint TEXT, resource_id TEXT,
          UNIQUE(actor_id,scope,operation_key));
        CREATE INDEX IF NOT EXISTS processes_ready ON processes(next_attempt,created_at) WHERE status NOT IN ('Completed','Failed');
        CREATE INDEX IF NOT EXISTS processes_checkpoint ON processes(expected_checkpoint,resource_id) WHERE status NOT IN ('Completed','Failed');
        CREATE TABLE IF NOT EXISTS process_checkpoints (
          checkpoint TEXT NOT NULL, resource_id TEXT NOT NULL, error TEXT, PRIMARY KEY(checkpoint,resource_id));
        CREATE TABLE IF NOT EXISTS process_history (
          ordinal BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY, process_id TEXT NOT NULL REFERENCES processes(id),
          status TEXT NOT NULL, attempt INTEGER NOT NULL, error TEXT, recorded_at TIMESTAMPTZ NOT NULL);
        """, ct);

    public Task<ProcessSnapshot> AcceptAsync(Process process, CancellationToken ct) => database.RunAsync(async (connection, transaction, token) =>
    {
        var state = process.Snapshot;
        await using (var insert = Command(connection, transaction, """
            INSERT INTO processes(id,actor_id,scope,operation_key,fingerprint,snapshot,status,created_at,next_attempt,expected_checkpoint)
            VALUES(@id,@actor,@scope,@key,@fingerprint,@snapshot,@status,@created,@next,@checkpoint)
            ON CONFLICT(actor_id,scope,operation_key) DO NOTHING
            """, ("id",state.Id),("actor",state.ActorId),("scope",state.Scope),("key",state.Key),("fingerprint",state.Fingerprint),
            ("snapshot",JsonSerializer.Serialize(state,Json)),("status",state.Status.ToString()),("created",state.CreatedAt), ("next",state.NextAttempt),("checkpoint",state.Checkpoint)))
            await insert.ExecuteNonQueryAsync(token);
        await using var read = Command(connection,transaction,"SELECT snapshot FROM processes WHERE actor_id=@actor AND scope=@scope AND operation_key=@key FOR UPDATE",
            ("actor",state.ActorId),("scope",state.Scope),("key",state.Key));
        var stored = JsonSerializer.Deserialize<ProcessSnapshot>((string)(await read.ExecuteScalarAsync(token))!,Json)!;
        if (stored.Fingerprint != state.Fingerprint) throw new ProcessKeyConflictException();
        return stored;
    }, ct);

    public async Task<ProcessSnapshot?> FindAsync(string id,string actorId,CancellationToken ct)
    {
        await using var connection=await database.OpenAsync(ct);
        await using var command=Command(connection,null,"SELECT snapshot FROM processes WHERE id=@id AND (actor_id=@actor OR @actor='*')",("id",id),("actor",actorId));
        return await ReadAsync(command,ct);
    }
    public Task<ProcessSnapshot?> ClaimAsync(string? id,string worker,DateTimeOffset now,TimeSpan lease,CancellationToken ct) =>
        database.RunAsync<ProcessSnapshot?>(async (connection,transaction,token) =>
        {
            await using var command=Command(connection,transaction,"""
                SELECT snapshot FROM processes WHERE (@id IS NULL OR id=@id) AND status NOT IN ('Completed','Failed')
                AND next_attempt<=@now AND (lease_until IS NULL OR lease_until<=@now)
                ORDER BY created_at,id LIMIT 1 FOR UPDATE SKIP LOCKED
                """,("id",id),("now",now));
            var state=await ReadAsync(command,token);
            if(state is null) return null;
            var process=Process.Restore(state);
            if(!process.TryClaim(worker,now,lease)) return null;
            await SaveAsync(connection,transaction,process,now,token); return process.Snapshot;
        },ct);

    public Task FinishAsync(string id,string worker,Execution execution,DateTimeOffset now,TimeSpan retry,TimeSpan timeout,CancellationToken ct) =>
        database.RunAsync(async (connection,transaction,token) =>
        {
            var process=await LoadOwnedAsync(connection,transaction,id,worker,now,token);
            if(process is null) return false;
            if(execution.Retryable) process.Retry(worker,execution.Error??"DEPENDENCY_UNAVAILABLE",now,retry);
            else if(execution.Result.StatusCode>=400)
                process.Reject(worker,execution.Result,execution.Error??"BUSINESS_REJECTED",execution.Compensate,now);
            else
            {
                var confirmed=false; string? rejected=null;
                if(process.Snapshot.Checkpoint is { } point && execution.Result.ResourceId is { } resource)
                {
                    await using var checkpoint=Command(connection,transaction,"SELECT COALESCE(error,'') FROM process_checkpoints WHERE checkpoint=@point AND resource_id=@resource",("point",point),("resource",resource));
                    var outcome=await checkpoint.ExecuteScalarAsync(token) as string;
                    confirmed=outcome is not null;
                    if(!string.IsNullOrEmpty(outcome)) rejected=outcome;
                }
                if(rejected is not null) process.Reject(worker,Failure(execution.Result.ResourceId!,rejected),rejected,false,now);
                else process.Succeed(worker,execution.Result,confirmed,now,timeout);
            }
            await SaveAsync(connection,transaction,process,now,token); return true;
        },ct);

    public Task CompensatedAsync(string id,string worker,DateTimeOffset now,CancellationToken ct) => database.RunAsync(async (connection,transaction,token) =>
    {
        var process=await LoadOwnedAsync(connection,transaction,id,worker,now,token);
        if(process is null) return false;
        process.Compensated(worker,now); await SaveAsync(connection,transaction,process,now,token); return true;
    },ct);

    public Task ConfirmAsync(string point,string resource,CancellationToken ct) => RecordCheckpointAsync(point,resource,null,ct);
    public async Task ConfirmOperationAsync(string operationId,int version,string point,string resource,CancellationToken ct)
    {
        var process=await FindAsync(operationId,"*",ct);
        if(process is null || process.Version!=version || process.Checkpoint!=point ||
            process.Result?.ResourceId is { } expected && expected!=resource)
            throw new InvalidOperationException("Checkpoint acknowledgement does not match its process and version.");
        await ConfirmAsync(point,resource,ct);
    }
    public Task RejectCheckpointAsync(string point,string resource,string error,CancellationToken ct) => RecordCheckpointAsync(point,resource,error,ct);
    private Task RecordCheckpointAsync(string point,string resource,string? error,CancellationToken ct) => database.RunAsync(async (connection,transaction,token) =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(point); ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        await using(var record=Command(connection,transaction,"INSERT INTO process_checkpoints(checkpoint,resource_id,error) VALUES(@point,@resource,@error) ON CONFLICT DO NOTHING",("point",point),("resource",resource),("error",error)))
            await record.ExecuteNonQueryAsync(token);
        await using(var prior=Command(connection,transaction,"SELECT error FROM process_checkpoints WHERE checkpoint=@point AND resource_id=@resource",("point",point),("resource",resource)))
            error=await prior.ExecuteScalarAsync(token) as string;
        var states=new List<ProcessSnapshot>();
        await using(var read=Command(connection,transaction,"SELECT snapshot FROM processes WHERE expected_checkpoint=@point AND resource_id=@resource AND status NOT IN ('Completed','Failed') FOR UPDATE",("point",point),("resource",resource)))
        await using(var reader=await read.ExecuteReaderAsync(token))
            while(await reader.ReadAsync(token)) states.Add(JsonSerializer.Deserialize<ProcessSnapshot>(reader.GetString(0),Json)!);
        foreach(var state in states)
        {
            var process=Process.Restore(state);
            if(error is null?process.Confirm(point,resource):process.RejectConfirmation(point,resource,error,Failure(resource,error)))
                await SaveAsync(connection,transaction,process,DateTimeOffset.UtcNow,token);
        }
        return true;
    },ct);

    private static async Task<Process?> LoadOwnedAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string id,string worker,DateTimeOffset now,CancellationToken ct)
    {
        await using var command=Command(connection,transaction,"SELECT snapshot FROM processes WHERE id=@id FOR UPDATE",("id",id));
        var state=await ReadAsync(command,ct);
        return state is not null && state.LeaseToken==worker && state.LeaseUntil>now && state.Status is not (ProcessStatus.Completed or ProcessStatus.Failed) ? Process.Restore(state) : null;
    }
    private static async Task SaveAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,Process process,DateTimeOffset now,CancellationToken ct)
    {
        var s=process.Snapshot;
        await using(var command=Command(connection,transaction,"UPDATE processes SET snapshot=@snapshot,status=@status,next_attempt=@next,lease_until=@lease,resource_id=@resource WHERE id=@id",
            ("id",s.Id),("snapshot",JsonSerializer.Serialize(s,Json)),("status",s.Status.ToString()),("next",s.NextAttempt),("lease",s.LeaseUntil),("resource",s.Result?.ResourceId)))
            await command.ExecuteNonQueryAsync(ct);
        await using var history=Command(connection,transaction,"INSERT INTO process_history(process_id,status,attempt,error,recorded_at) VALUES(@id,@status,@attempt,@error,@now)",
            ("id",s.Id),("status",s.Status.ToString()),("attempt",s.Attempts),("error",s.Error),("now",now));
        await history.ExecuteNonQueryAsync(ct);
    }
    private static async Task<ProcessSnapshot?> ReadAsync(NpgsqlCommand command,CancellationToken ct) =>
        await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<ProcessSnapshot>(json,Json) : null;
    private static ProcessResult Failure(string resource,string error) => new(422,"application/json",JsonSerializer.SerializeToUtf8Bytes(new {code=error,message=error}),resource);
    private static NpgsqlCommand Command(NpgsqlConnection connection,NpgsqlTransaction? transaction,string sql,params (string Name,object? Value)[] parameters)
    {
        var command=new NpgsqlCommand(sql,connection,transaction);
        foreach(var (name,value) in parameters)
        {
            if(value is null) command.Parameters.Add(new NpgsqlParameter(name,NpgsqlTypes.NpgsqlDbType.Text) { Value=DBNull.Value });
            else command.Parameters.AddWithValue(name,value);
        }
        // A missing timestamp needs its actual PostgreSQL type rather than an unknown text parameter.
        if(command.Parameters.Contains("lease") && command.Parameters["lease"].Value is DBNull) command.Parameters["lease"].NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.TimestampTz;
        return command;
    }
}
