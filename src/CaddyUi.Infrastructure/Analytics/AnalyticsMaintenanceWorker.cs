using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Infrastructure.Analytics;

public sealed class AnalyticsMaintenanceWorker : BackgroundService
{
    private readonly AnalyticsIngestionOptions _options;
    private readonly AnalyticsIngestionStore _store;
    private readonly ILogger<AnalyticsMaintenanceWorker> _logger;

    public AnalyticsMaintenanceWorker(
        AnalyticsIngestionOptions options,
        AnalyticsIngestionStore store,
        ILogger<AnalyticsMaintenanceWorker> logger)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(_options.MaintenanceIntervalMinutes));

        do
        {
            try
            {
                await _store.RunMaintenanceAsync(
                    _options,
                    DateTimeOffset.UtcNow,
                    stoppingToken);
                _logger.LogInformation("Caddy analytics maintenance completed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Caddy analytics maintenance failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
