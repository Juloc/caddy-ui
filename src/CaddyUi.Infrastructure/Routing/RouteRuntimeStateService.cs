using System.Security.Cryptography;
using System.Text;
using CaddyUi.Application.Routing;

namespace CaddyUi.Infrastructure.Routing;

public sealed class RouteRuntimeStateService
{
    private readonly RouteManagementStore _routeStore;
    private readonly RouteApplyStore _applyStore;
    private readonly CaddyApplyService _applyService;
    private readonly RoutingOptions _options;

    public RouteRuntimeStateService(
        RouteManagementStore routeStore,
        RouteApplyStore applyStore,
        CaddyApplyService applyService,
        RoutingOptions options)
    {
        _routeStore = routeStore;
        _applyStore = applyStore;
        _applyService = applyService;
        _options = options;
    }

    public async Task<RoutingRuntimeState> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var sources = await _routeStore.LoadCompilerSourcesAsync(cancellationToken);
        var compiler = new CaddyRouteCompiler(
            _options.AllowCustomRoutes,
            _options.PortalUpstream);
        var desired = compiler.Compile(sources);

        var activeContent = await _applyService.ReadManagedContentAsync(cancellationToken);
        var normalizedActiveContent = activeContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        var activeDigest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedActiveContent)));
        var activeRevision = activeContent.Length == 0
            ? null
            : await _applyStore.GetRevisionByDigestAsync(activeDigest, cancellationToken);

        var latestOperation = (await _applyStore.ListOperationsAsync(1, cancellationToken))
            .FirstOrDefault();
        var lastApplyFailed = false;
        var lastApplyError = string.Empty;
        if (latestOperation is { State: "failed", RouteRevisionId: Guid failedRevisionId })
        {
            var failedRevision = await _applyStore.GetRevisionAsync(failedRevisionId, cancellationToken);
            if (failedRevision is not null &&
                string.Equals(
                    failedRevision.Digest,
                    desired.Digest,
                    StringComparison.OrdinalIgnoreCase))
            {
                lastApplyFailed = true;
                lastApplyError = latestOperation.Error;
            }
        }

        return RouteRuntimeStateResolver.Resolve(
            sources.Select(source => source.Route).ToArray(),
            desired.Digest,
            desired.ManifestJson,
            activeDigest,
            activeRevision?.Id,
            activeRevision?.ManifestJson,
            activeContentIsEmpty: activeContent.Length == 0,
            lastApplyFailed,
            lastApplyError);
    }
}
