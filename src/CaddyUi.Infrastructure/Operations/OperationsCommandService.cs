using System.Text.Json;

namespace CaddyUi.Infrastructure.Operations;

public sealed class OperationsCommandService
{
    private readonly OperationsStore _store;
    private readonly DdnsService _ddns;
    private readonly DnsProviderRuntimeService _providers;
    private readonly HealthProbeService _health;
    private readonly BackupDiagnosticsService _backups;
    private readonly NotificationDispatcher _notifications;

    public OperationsCommandService(
        OperationsStore store,
        DdnsService ddns,
        DnsProviderRuntimeService providers,
        HealthProbeService health,
        BackupDiagnosticsService backups,
        NotificationDispatcher notifications)
    {
        _store = store;
        _ddns = ddns;
        _providers = providers;
        _health = health;
        _backups = backups;
        _notifications = notifications;
    }

    public async Task<ProviderOperationResult> RunJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = (await _store.ListJobsAsync(cancellationToken)).FirstOrDefault(item => item.Id == jobId) ??
            throw new InvalidOperationException("The scheduled job does not exist.");
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
            details = JsonSerializer.Serialize(new { manual = true, job.JobType, result.ExternalId });
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

        return result;
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
