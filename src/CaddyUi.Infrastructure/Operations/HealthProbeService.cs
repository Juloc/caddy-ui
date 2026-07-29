using System.Diagnostics;

namespace CaddyUi.Infrastructure.Operations;

public sealed class HealthProbeService
{
    private readonly OperationsStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NotificationDispatcher _notifications;

    public HealthProbeService(
        OperationsStore store,
        IHttpClientFactory httpClientFactory,
        NotificationDispatcher notifications)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _notifications = notifications;
    }

    public async Task<ProviderOperationResult> CheckAsync(
        HealthTargetRecord target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stopwatch = Stopwatch.StartNew();
        int? statusCode = null;
        ProviderOperationResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(target.TimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            request.Headers.UserAgent.ParseAdd("Caddy-UI-Health/2.0");
            using var response = await _httpClientFactory.CreateClient("health-probes")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            statusCode = (int)response.StatusCode;
            result = statusCode >= target.ExpectedStatusMin && statusCode <= target.ExpectedStatusMax
                ? ProviderOperationResult.Success($"HTTP {statusCode} in {stopwatch.Elapsed.TotalMilliseconds:N0} ms.")
                : ProviderOperationResult.Failure(
                    $"Expected HTTP {target.ExpectedStatusMin}-{target.ExpectedStatusMax}, received {statusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = ProviderOperationResult.Failure($"Health request timed out after {target.TimeoutSeconds} seconds.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = ProviderOperationResult.Failure(exception.Message);
        }

        stopwatch.Stop();
        await _store.RecordHealthCheckAsync(
            target.Id,
            result,
            statusCode,
            stopwatch.Elapsed.TotalMilliseconds,
            cancellationToken);
        if (!result.Succeeded && target.LastStatus != "unhealthy")
        {
            await _notifications.NotifyAsync(
                new SystemNotification(
                    "error",
                    "health.unhealthy",
                    $"Healthcheck fehlgeschlagen: {target.Name}",
                    result.Message,
                    "health_target",
                    target.Id.ToString("D")),
                cancellationToken);
        }

        return result;
    }

    public async Task<ProviderOperationResult> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var targets = await _store.ListHealthTargetsAsync(cancellationToken);
        var enabled = targets.Where(target => target.Enabled).ToArray();
        var failures = new List<string>();
        foreach (var target in enabled)
        {
            var result = await CheckAsync(target, cancellationToken);
            if (!result.Succeeded)
            {
                failures.Add($"{target.Name}: {result.Message}");
            }
        }

        return failures.Count == 0
            ? ProviderOperationResult.Success($"Checked {enabled.Length} health targets successfully.")
            : ProviderOperationResult.Failure(string.Join(" ", failures));
    }
}
