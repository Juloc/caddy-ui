using CaddyUi.Application.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Infrastructure.Security;

public sealed class IpIntelligenceRefreshWorker : BackgroundService
{
    private readonly IpSecurityOptions _options;
    private readonly IpIntelligenceStore _store;
    private readonly IIpIntelligenceProvider _provider;
    private readonly ILogger<IpIntelligenceRefreshWorker> _logger;

    public IpIntelligenceRefreshWorker(
        IpSecurityOptions options,
        IpIntelligenceStore store,
        IIpIntelligenceProvider provider,
        ILogger<IpIntelligenceRefreshWorker> logger)
    {
        _options = options;
        _store = store;
        _provider = provider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IntelligenceEnabled)
        {
            _logger.LogInformation("IP intelligence background refresh is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.RefreshIntervalSeconds));
        do
        {
            try
            {
                await _store.DiscoverRecentAddressesAsync(stoppingToken);
                var requests = await _store.ListReadyAsync(stoppingToken);
                foreach (var request in requests)
                {
                    var result = await _provider.LookupAsync(request.Address, stoppingToken);
                    await _store.CompleteAsync(result, request.Attempt, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "IP intelligence refresh failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
