using Olympiad.Review.Application;
namespace Olympiad.Review.Api;
internal sealed class BackgroundCompletion(CompletionRefresh refresh, ILogger<BackgroundCompletion> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await refresh.Execute(stoppingToken);
                foreach (var failure in result.Failures)
                    logger.LogWarning("Completion refresh skipped game {GameId}: {Code} ({Kind})",
                        failure.GameId, failure.Code, failure.Kind);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Completion refresh will be retried"); }
            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
