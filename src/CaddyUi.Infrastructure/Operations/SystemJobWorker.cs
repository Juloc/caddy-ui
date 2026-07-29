using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Infrastructure.Operations;

public sealed class SystemJobWorker : BackgroundService
{
    private readonly OperationsOptions _options;
    private readonly OperationsStore _store;
    private readonly DdnsService _ddns;
    private readonly DnsProviderRuntimeService _providers;
    private readonly HealthProbeService _health;
    private readonly BackupDiagnosticsService _backups;
    private readonly NotificationDispatcher _notifications;
    private readonly ILogger<SystemJobWorker> _logger;
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public SystemJobWorker(
        OperationsOptions options,
        OperationsStore store,
        DdnsService ddns,
        DnsProviderRuntimeService providers,
        HealthProbeService health,
        BackupDiagnosticsService backups,
        NotificationDispatcher notifications,
        ILogger<SystemJobWorker> logger)
    {
        _options = options;
        _store = store;
        _ddns = ddns;
        _providers = providers;
        _health = health;
        _backups = backups;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.WorkerEnabled)
                {
                    await ProcessDdnsAsync(stoppingToken);
                    await ProcessJobAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Phase 8 operations worker iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessDdnsAsync(CancellationToken cancellationToken)
    {
        var target = await _store.ClaimDueDdnsTargetAsync(_workerId, cancellationToken);
        if (target is not null)
        {
            await _ddns.RunAsync(target, cancellationToken);
        }
    }

    private async Task ProcessJobAsync(CancellationToken cancellationToken)
    {
        var job = await _store.ClaimDueJobAsync(_workerId, cancellationToken);
        if (job is null)
        {
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var runId = await _store.StartJobRunAsync(job.Id, correlationId, cancellationToken);
        ProviderOperationResult result;
        var details = "{}";
        try
        {
            var config = OperationsJson.ReadStringObject(job.ConfigJson);
            result = job.JobType switch
            {
                "ddns" => await _ddns.RunAllAsync(cancellationToken),
                "health" => await _health.CheckAllAsync(cancellationToken),
                "backup" => await _backups.CreateBackupAsync(cancellationToken),
                "provider-test" => await RunProviderTestAsync(config, cancellationToken),
                _ => ProviderOperationResult.Failure($"Unsupported job type '{job.JobType}'."),
            };
            details = JsonSerializer.Serialize(new { job.JobType, result.ExternalId });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = ProviderOperationResult.Failure(exception.Message);
        }

        await _store.CompleteJobRunAsync(job.Id, runId, result, details, cancellationToken);
        if (!result.Succeeded)
        {
            await _notifications.NotifyAsync(
                new SystemNotification(
                    "error",
                    "job.failed",
                    $"Job fehlgeschlagen: {job.Name}",
                    result.Message,
                    "scheduled_job",
                    job.Id.ToString("D")),
                cancellationToken);
        }
    }

    private Task<ProviderOperationResult> RunProviderTestAsync(
        IReadOnlyDictionary<string, string> config,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(OperationsJson.Required(config, "provider_id"), out var providerId))
        {
            throw new InvalidOperationException("The provider-test job requires a valid provider_id.");
        }

        return _providers.TestProviderAsync(
            providerId,
            OperationsJson.Required(config, "domain"),
            cancellationToken);
    }
}
