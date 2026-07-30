using System.Data;
using System.Text;
using System.Text.Json;
using CaddyUi.Application.Routing;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaddyUi.Infrastructure.Operations;

public sealed class CaddyCertificateSourceRefreshWorker : BackgroundService
{
    private const string ProtectedSecretPrefix = "secret://protected/";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly OperationsOptions _options;
    private readonly ISecretReferenceResolver _secretResolver;
    private readonly ILogger<CaddyCertificateSourceRefreshWorker> _logger;

    public CaddyCertificateSourceRefreshWorker(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        OperationsOptions options,
        ISecretReferenceResolver secretResolver,
        ILogger<CaddyCertificateSourceRefreshWorker> logger)
    {
        _contextFactory = contextFactory;
        _options = options;
        _secretResolver = secretResolver;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Initial managed-domain certificate source refresh was unavailable. The background worker will retry.");
        }

        await base.StartAsync(cancellationToken);
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

            await Task.Delay(RefreshInterval, stoppingToken);
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
                   providers.id,
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
                var providerId = reader.GetGuid(4);
                var providerType = reader.GetString(5);
                var secretReferences = await MaterializeProtectedSecretsAsync(
                    providerId,
                    ReadObject(reader.GetString(8)),
                    cancellationToken);
                provider = new CaddyDnsProviderSource(
                    providerType,
                    reader.GetBoolean(6),
                    _options.InstalledCaddyDnsModules.Contains(providerType),
                    ReadObject(reader.GetString(7)),
                    secretReferences);
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

    private async Task<IReadOnlyDictionary<string, string>> MaterializeProtectedSecretsAsync(
        Guid providerId,
        IReadOnlyDictionary<string, string> references,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in references)
        {
            var reference = pair.Value.Trim();
            if (!reference.StartsWith(ProtectedSecretPrefix, StringComparison.Ordinal))
            {
                result[pair.Key] = reference;
                continue;
            }

            var value = await _secretResolver.ResolveAsync(reference, cancellationToken);
            var runtimeName = $"CADDY_UI_PROVIDER_{providerId:N}_{SafeEnvironmentName(pair.Key)}"
                .ToUpperInvariant();
            var path = Path.Combine(_options.ProviderSecretDirectory, runtimeName);
            await WriteSecretAtomicallyAsync(path, value, cancellationToken);
            result[pair.Key] = "secret://env/" + runtimeName;
        }

        return result;
    }

    private static async Task WriteSecretAtomicallyAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("The runtime secret path has no directory.");
        Directory.CreateDirectory(directory);
        TrySetMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, value, new UTF8Encoding(false), cancellationToken);
            TrySetMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, fullPath, overwrite: true);
            TrySetMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TrySetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string SafeEnvironmentName(string value)
    {
        var safe = new string(value
            .Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_')
            .ToArray());
        return safe.Length == 0 ? "SECRET" : safe;
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
