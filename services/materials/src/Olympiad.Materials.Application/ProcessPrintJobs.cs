using Olympiad.Materials.Domain;

namespace Olympiad.Materials.Application;

public sealed class ProcessPrintJobs(IMaterialsTransactions transactions, IPdfRenderer renderer, IClock clock)
{
    public async Task<bool> ExecuteNextAsync(CancellationToken cancellationToken)
    {
        var now = await clock.UtcNowAsync(cancellationToken);
        var token = Guid.NewGuid().ToString();
        var job = await transactions.ExecuteAsync(async (s, ct) =>
        {
            var candidate = await s.PrintJobs.FindNextPendingAsync(now, ct);
            if (candidate is null || !candidate.TryStart(token, now, TimeSpan.FromMinutes(10))) return null;
            await s.PrintJobs.SaveAsync(candidate, ct); return candidate;
        }, cancellationToken);
        if (job is null) return false;
        byte[] bytes;
        try { bytes = await renderer.RenderAsync(job.Snapshot, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await transactions.ExecuteAsync(async (s, ct) =>
            {
                var current = await s.PrintJobs.FindAsync(job.Id, ct) ?? throw new InvalidOperationException("Missing print job.");
                if (current.ProcessingToken != token) return false;
                current.Fail(token, new("PDF_RENDER_FAILED", $"PDF rendering failed: {exception.GetType().Name}"));
                await s.PrintJobs.SaveAsync(current, ct); return true;
            }, cancellationToken);
            return true;
        }
        var completedAt = await clock.UtcNowAsync(cancellationToken);
        await transactions.ExecuteAsync(async (s, ct) =>
        {
            var current = await s.PrintJobs.FindAsync(job.Id, ct) ?? throw new InvalidOperationException("Missing print job.");
            if (current.ProcessingToken != token) return false;
            var artifactUrl = $"/print-jobs/{job.Id}/artifact";
            current.Complete(token, artifactUrl);
            await s.PrintJobs.StoreArtifactAsync(job.Id, bytes, ct);
            await s.PrintJobs.SaveAsync(current, ct);
            await s.Outbox.AddAsync(new(Guid.NewGuid().ToString(), "PrintPdfPrepared", completedAt, current.Id,
                current.GameId, current.TeamId, PrintJobId: current.Id, ArtifactUrl: artifactUrl, TemplateKey: current.Snapshot.Template.Key), ct);
            return true;
        }, cancellationToken);
        return true;
    }
}

