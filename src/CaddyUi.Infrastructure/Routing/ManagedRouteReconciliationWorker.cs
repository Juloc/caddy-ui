using System.Net;
using CaddyUi.Application.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Infrastructure.Routing;

public sealed class ManagedRouteReconciliationWorker : BackgroundService
{
    private static readonly TimeSpan CaddyProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconciliationRetryInterval = TimeSpan.FromSeconds(5);
    private const int MaximumReconciliationAttempts = 12;

    private readonly RouteManagementStore _store;
    private readonly CaddyApplyService _applyService;
    private readonly RoutingOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<ManagedRouteReconciliationWorker> _logger;
    private readonly Uri _caddyConfigurationUri;

    public ManagedRouteReconciliationWorker(
        RouteManagementStore store,
        CaddyApplyService applyService,
        RoutingOptions options,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IHostApplicationLifetime applicationLifetime,
        ILogger<ManagedRouteReconciliationWorker> logger)
    {
        _store = store;
        _applyService = applyService;
        _options = options;
        _httpClientFactory = httpClientFactory;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _caddyConfigurationUri = BuildAdminConfigurationUri(
            configuration["CADDY_ADMIN_URL"]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.WriteMode != RouteWriteMode.Active)
        {
            return;
        }

        await WaitForApplicationStartAsync(stoppingToken);
        await WaitForCaddyAsync(stoppingToken);

        for (var attempt = 1; attempt <= MaximumReconciliationAttempts; attempt++)
        {
            try
            {
                var completed = await TryReconcileAsync(stoppingToken);
                if (completed)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Managed route startup reconciliation attempt {Attempt} of {MaximumAttempts} failed.",
                    attempt,
                    MaximumReconciliationAttempts);
            }

            if (attempt < MaximumReconciliationAttempts)
            {
                await Task.Delay(ReconciliationRetryInterval, stoppingToken);
            }
        }

        _logger.LogError(
            "Managed routes could not be reconciled after {Attempts} startup attempts. The existing Caddy configuration remains active.",
            MaximumReconciliationAttempts);
    }

    private async Task<bool> TryReconcileAsync(CancellationToken cancellationToken)
    {
        var sources = await _store.LoadCompilerSourcesAsync(cancellationToken);
        var compiler = new CaddyRouteCompiler(
            _options.AllowCustomRoutes,
            _options.PortalUpstream);
        var compilation = compiler.Compile(sources);
        if (!compilation.CertificateReadyForActiveApply)
        {
            _logger.LogInformation(
                "Managed route startup reconciliation is waiting for the certificate and DNS source registry.");
            return false;
        }

        var current = await _applyService.ReadCurrentContentAsync(cancellationToken);
        if (!RequiresReconciliation(current, compilation.Content))
        {
            _logger.LogInformation("Managed routes already match the current renderer.");
            return true;
        }

        var revision = await _store.CreateRevisionAsync(
            compilation,
            "Automatic startup reconciliation after a Caddy UI renderer update",
            ManagementActor.System,
            cancellationToken);
        var result = await _applyService.ApplyAsync(
            revision.Id,
            ManagementActor.System,
            cancellationToken);
        _logger.LogInformation(
            "Managed routes were reconciled automatically. Operation {OperationId} completed with state {State}.",
            result.OperationId,
            result.State);
        return true;
    }

    private async Task WaitForApplicationStartAsync(CancellationToken cancellationToken)
    {
        if (_applicationLifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = _applicationLifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            started);
        await started.Task.WaitAsync(cancellationToken);
    }

    private async Task WaitForCaddyAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync(
                    _caddyConfigurationUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(CaddyProbeInterval, cancellationToken);
        }
    }

    public static bool RequiresReconciliation(string current, string desired)
    {
        return !string.Equals(
            NormalizeLineEndings(current),
            NormalizeLineEndings(desired),
            StringComparison.Ordinal);
    }

    public static Uri BuildAdminConfigurationUri(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value)
            ? "http://caddy:2019"
            : value.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("CADDY_ADMIN_URL must be a valid HTTP(S) address.");
        }

        return new UriBuilder(uri)
        {
            Path = "/config/",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
