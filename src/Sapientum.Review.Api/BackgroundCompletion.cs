using Sapientum.Review.Application;
namespace Sapientum.Review.Api;
internal sealed class BackgroundCompletion(CompletionRefresh refresh, ILogger<BackgroundCompletion> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await refresh.Execute(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Completion refresh will be retried"); }
            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
