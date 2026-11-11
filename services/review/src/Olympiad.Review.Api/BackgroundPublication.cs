using Olympiad.Review.Application;
namespace Olympiad.Review.Api;
internal sealed class BackgroundPublication(IOutboxDelivery dispatcher, ILogger<BackgroundPublication> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await dispatcher.Drain(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Outbox retained for retry"); }
            try { await Task.Delay(100, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
