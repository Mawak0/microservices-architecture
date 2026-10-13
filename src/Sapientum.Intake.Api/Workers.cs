using Sapientum.Intake.Application;

namespace Sapientum.Intake.Api;

internal sealed class OutboxWorker(DeliverPendingEvent delivery, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (await delivery.ExecuteAsync(ct)) continue; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Outbox publication will be retried."); }
            await Task.Delay(100, ct);
        }
    }
}

internal sealed class SubmissionRecoveryWorker(RecoverPendingSubmission recovery, ILogger<SubmissionRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (await recovery.ExecuteAsync(ct)) continue; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Interrupted verified image upload will be retried."); }
            await Task.Delay(250, ct);
        }
    }
}
