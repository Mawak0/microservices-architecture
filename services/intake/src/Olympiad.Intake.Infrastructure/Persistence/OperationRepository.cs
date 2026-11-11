using System.Text.Json;
using Olympiad.Intake.Application;

namespace Olympiad.Intake.Infrastructure.Persistence;

internal sealed class OperationRepository(PostgresScope db) : IOperationRepository
{
    public async Task<SavedOperation?> FindAsync(OperationIdentity identity, CancellationToken ct)
    {
        await using var command = db.Command("SELECT fingerprint,result_json FROM operations WHERE scope=@scope AND operation_key=@key", ("@scope", identity.Scope), ("@key", identity.Key));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), JsonSerializer.Deserialize<OperationResult>(reader.GetString(1))!) : null;
    }
    public Task SaveAsync(OperationIdentity identity, OperationResult result, CancellationToken ct) => db.ExecuteAsync("""
        INSERT INTO operations(scope,operation_key,fingerprint,result_json) VALUES(@scope,@key,@fingerprint,@result)
        """, ct, ("@scope", identity.Scope), ("@key", identity.Key), ("@fingerprint", identity.Fingerprint), ("@result", JsonSerializer.Serialize(result)));
}
