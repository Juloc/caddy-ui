using System.Data;
using System.Text.Json;
using CaddyUi.Application.Routing;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Infrastructure.Operations;

public sealed class CaddyCertificateSourceRefreshWorker : BackgroundService
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly OperationsOptions _options;
    private readonly ILogger<CaddyCertificateSourceRefreshWorker> _logger;

    public CaddyCertificateSourceRefreshWorker(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        OperationsOptions options,
        ILogger<CaddyCertificateSourceRefreshWorker> logger)
    {
        _contextFactory = contextFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not refresh managed-domain certificate sources.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT domains.id,
                   domains.name,
                   domains.default_certificate_mode,
                   domains.config_json::text,
                   providers.provider_type,
                   providers.enabled,
                   providers.config_json::text,
                   providers.secret_references_json::text
            FROM caddy_ui.managed_domains AS domains
            LEFT JOIN caddy_ui.dns_providers AS providers
              ON providers.id = domains.dns_provider_id
            WHERE domains.enabled
            ORDER BY domains.id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sources = new List<CaddyDomainCertificateSource>();
        while (await reader.ReadAsync(cancellationToken))
        {
            CaddyDnsProviderSource? provider = null;
            if (!reader.IsDBNull(4))
            {
                var providerType = reader.GetString(4);
                provider = new CaddyDnsProviderSource(
                    providerType,
                    reader.GetBoolean(5),
                    _options.InstalledCaddyDnsModules.Contains(providerType),
                    ReadObject(reader.GetString(6)),
                    ReadObject(reader.GetString(7)));
            }

            var mode = reader.GetString(2);
            var plan = ReadCertificatePlan(mode, reader.GetString(3));
            sources.Add(new CaddyDomainCertificateSource(
                reader.GetGuid(0),
                reader.GetString(1),
                mode,
                plan.Wildcard,
                plan.BaseDomain,
                provider));
        }

        CaddyCertificateSourceRegistry.Replace(sources);
    }

    private static (bool Wildcard, bool BaseDomain) ReadCertificatePlan(string mode, string json)
    {
        var defaultWildcard = string.Equals(mode, "wildcard", StringComparison.OrdinalIgnoreCase);
        var wildcard = defaultWildcard;
        var baseDomain = defaultWildcard;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("certificatePlan", out var plan) ||
                plan.ValueKind != JsonValueKind.Object)
            {
                return (wildcard, baseDomain);
            }

            if (plan.TryGetProperty("wildcard", out var wildcardProperty) &&
                wildcardProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                wildcard = wildcardProperty.GetBoolean();
            }

            if (plan.TryGetProperty("baseDomain", out var baseProperty) &&
                baseProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                baseDomain = baseProperty.GetBoolean();
            }
        }
        catch (JsonException)
        {
        }

        return (wildcard, baseDomain);
    }

    private static IReadOnlyDictionary<string, string> ReadObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText(),
            StringComparer.OrdinalIgnoreCase);
    }
}
