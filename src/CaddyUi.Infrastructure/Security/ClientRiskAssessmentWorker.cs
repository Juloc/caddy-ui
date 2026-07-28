using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Infrastructure.Security;

public sealed class ClientRiskAssessmentWorker : BackgroundService
{
    private readonly IpSecurityOptions _options;
    private readonly ClientRiskAssessmentStore _store;
    private readonly ILogger<ClientRiskAssessmentWorker> _logger;

    public ClientRiskAssessmentWorker(
        IpSecurityOptions options,
        ClientRiskAssessmentStore store,
        ILogger<ClientRiskAssessmentWorker> logger)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RiskAssessmentEnabled)
        {
            _logger.LogInformation("Client risk assessment is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(_options.RiskRefreshMinutes));
        do
        {
            try
            {
                var count = await _store.AssessReadyClientsAsync(stoppingToken);
                _logger.LogDebug("Stored {AssessmentCount} client risk assessments.", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Client risk assessment failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
