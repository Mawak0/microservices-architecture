using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;

namespace Olympiad.Intake.Infrastructure.Persistence;

internal sealed class SessionRepository(SqliteScope db) : ISessionRepository
{
    public async Task<IntakeSession?> FindAsync(string gameId, CancellationToken ct)
    {
        await using var command = db.Command("SELECT admission_version,is_open FROM sessions WHERE game_id=$id", ("$id", gameId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? IntakeSession.Restore(gameId, reader.GetInt32(0), reader.GetBoolean(1)) : null;
    }
    public Task SaveAsync(IntakeSession session, CancellationToken ct) => db.ExecuteAsync("""
        INSERT INTO sessions(game_id,admission_version,is_open) VALUES($id,$version,$open)
        ON CONFLICT(game_id) DO UPDATE SET admission_version=excluded.admission_version,is_open=excluded.is_open
        """, ct, ("$id", session.GameId), ("$version", session.AdmissionVersion), ("$open", session.IsOpen));
}
