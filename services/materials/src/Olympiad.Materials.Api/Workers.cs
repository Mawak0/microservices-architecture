using Olympiad.Materials.Application;

namespace Olympiad.Materials.Api;

internal sealed class PrintWorker(ProcessPrintJobs processor, ILogger<PrintWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (await processor.ExecuteNextAsync(stoppingToken)) continue; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Print queue processing failed; persisted jobs will be retried"); }
            await Task.Delay(250, stoppingToken);
        }
    }
}
internal sealed class OutboxWorker(PublishOutbox publisher, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await publisher.ExecuteAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Outbox delivery failed; the event remains pending"); }
            await Task.Delay(250, stoppingToken);
        }
    }
}
