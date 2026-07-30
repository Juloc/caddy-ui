using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaddyUi.Application.Dns;
using CaddyUi.Domain.Certificates;
using CaddyUi.Domain.Domains;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Setup;

public sealed record GuidedSetupRequest(
    string ProviderMode,
    Guid? ExistingProviderId,
    string ProviderType,
    string ProviderLabel,
    IReadOnlyDictionary<string, string> ProviderSettings,
    IReadOnlyDictionary<string, string> ProviderSecretReferences,
    string DomainName,
    string DomainDisplayName,
    bool MakeDefaultDomain,
    bool RequestWildcardCertificate,
    bool RequestBaseCertificate,
    bool CreateRoute,
    string RouteName,
    string RouteSubdomain,
    string RouteKind,
    string RoutePathPrefix,
    string RouteCertificateMode,
    string UpstreamScheme,
    string UpstreamHost,
    int? UpstreamPort,
    string RedirectTarget,
    bool RedirectPermanent,
    int StaticStatusCode,
    string StaticBody,
    string CustomSnippet);

public sealed record GuidedSetupResult(
    Guid? ProviderId,
    Guid DomainId,
    Guid? RouteId,
    string DomainName,
    string? RouteHost);

public sealed partial class GuidedSetupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly RoutingOptions _routingOptions;

    public GuidedSetupService(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        RoutingOptions routingOptions)
    {
        _contextFactory = contextFactory;
        _routingOptions = routingOptions;
    }

    public async Task<GuidedSetupResult> ProvisionAsync(
        GuidedSetupRequest request,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var domainCertificateMode = request.RequestWildcardCertificate
            ? CertificateMode.Wildcard
            : CertificateMode.Individual;
        if (!request.RequestWildcardCertificate && !request.RequestBaseCertificate)
        {
            throw new ArgumentException("Mindestens Wildcard oder Basisdomain-Zertifikat muss ausgewählt sein.");
        }

        var domainId = Guid.NewGuid();
        var normalizedDomain = ManagedDomain.Create(
            request.DomainName,
            request.DomainDisplayName,
            domainCertificateMode,
            dnsProviderId: null,
            id: domainId);

        var providerMode = NormalizeProviderMode(request.ProviderMode);
        DnsProviderDefinition? providerDefinition = null;
        string providerSettingsJson = "{}";
        string providerSecretsJson = "{}";
        Guid? providerId = null;
        if (providerMode == "new")
        {
            providerDefinition = DnsProviderCatalog.Find(request.ProviderType) ??
                throw new ArgumentException("Der ausgewählte DNS-Provider wird nicht unterstützt.");
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderLabel);
            providerSettingsJson = NormalizeSettings(providerDefinition, request.ProviderSettings);
            providerSecretsJson = NormalizeSecretReferences(providerDefinition, request.ProviderSecretReferences);
            providerId = Guid.NewGuid();
        }
        else if (providerMode == "existing")
        {
            providerId = request.ExistingProviderId ??
                throw new ArgumentException("Ein vorhandener DNS-Provider muss ausgewählt werden.");
        }

        ManagedRouteDefinition? route = null;
        if (request.CreateRoute)
        {
            var kind = ManagedRouteDefinition.ParseKind(request.RouteKind);
            if (kind == ManagedRouteKind.Custom && !_routingOptions.AllowCustomRoutes)
            {
                throw new InvalidOperationException("Benutzerdefinierte Caddy-Routen sind deaktiviert.");
            }

            var configuration = RouteConfigurationDocument.Empty with
            {
                PathPrefix = request.RoutePathPrefix,
                Upstream = kind == ManagedRouteKind.Proxy
                    ? BuildUpstream(request.UpstreamScheme, request.UpstreamHost, request.UpstreamPort)
                    : string.Empty,
                RedirectTarget = request.RedirectTarget,
                RedirectPermanent = request.RedirectPermanent,
                StaticStatusCode = request.StaticStatusCode,
                StaticBody = request.StaticBody,
                CustomSnippet = request.CustomSnippet,
            };
            route = ManagedRouteDefinition.Create(
                Guid.NewGuid(),
                request.RouteName,
                domainId,
                normalizedDomain.Name,
                request.RouteSubdomain,
                kind,
                enabled: true,
                sortOrder: 0,
                ManagedRouteDefinition.ParseCertificateMode(request.RouteCertificateMode),
                accessGroupId: null,
                configuration);
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (providerMode == "existing")
            {
                await EnsureProviderExistsAsync(connection, transaction, providerId!.Value, cancellationToken);
            }
            else if (providerMode == "new")
            {
                await InsertProviderAsync(
                    connection,
                    transaction,
                    providerId!.Value,
                    providerDefinition!,
                    request.ProviderLabel,
                    providerSettingsJson,
                    providerSecretsJson,
                    cancellationToken);
                await InsertAuditAsync(
                    connection,
                    transaction,
                    actor,
                    "setup.provider.create",
                    "dns_provider",
                    providerId.Value.ToString("D"),
                    JsonSerializer.Serialize(new
                    {
                        providerDefinition!.Type,
                        label = request.ProviderLabel.Trim(),
                    }, JsonOptions),
                    cancellationToken);
            }

            var domainCount = await CountDomainsAsync(connection, transaction, cancellationToken);
            var makeDefault = request.MakeDefaultDomain || domainCount == 0;
            if (makeDefault)
            {
                await using var resetDefault = connection.CreateCommand();
                resetDefault.Transaction = transaction;
                resetDefault.CommandText = "UPDATE caddy_ui.managed_domains SET is_default = false WHERE is_default";
                await resetDefault.ExecuteNonQueryAsync(cancellationToken);
            }

            var domainConfig = JsonSerializer.Serialize(new
            {
                schema = "managed-domain-v1",
                certificatePlan = new
                {
                    wildcard = request.RequestWildcardCertificate,
                    baseDomain = request.RequestBaseCertificate,
                },
            }, JsonOptions);
            await using (var insertDomain = connection.CreateCommand())
            {
                insertDomain.Transaction = transaction;
                insertDomain.CommandText =
                    """
                    INSERT INTO caddy_ui.managed_domains(
                        id, name, display_name, enabled, is_default,
                        default_certificate_mode, dns_provider_id, config_json,
                        created_at, updated_at)
                    VALUES(
                        @id, @name, @display_name, true, @is_default,
                        @certificate_mode, @provider_id, CAST(@config_json AS jsonb),
                        @now, @now)
                    """;
                AddParameter(insertDomain, "id", domainId);
                AddParameter(insertDomain, "name", normalizedDomain.Name);
                AddParameter(insertDomain, "display_name", normalizedDomain.DisplayName);
                AddParameter(insertDomain, "is_default", makeDefault);
                AddParameter(insertDomain, "certificate_mode", normalizedDomain.DefaultCertificateMode.ToStorageValue());
                AddParameter(insertDomain, "provider_id", providerId);
                AddParameter(insertDomain, "config_json", domainConfig);
                AddParameter(insertDomain, "now", DateTimeOffset.UtcNow);
                await insertDomain.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "setup.domain.create",
                "managed_domain",
                domainId.ToString("D"),
                JsonSerializer.Serialize(new
                {
                    normalizedDomain.Name,
                    normalizedDomain.DisplayName,
                    providerId,
                    request.RequestWildcardCertificate,
                    request.RequestBaseCertificate,
                    makeDefault,
                }, JsonOptions),
                cancellationToken);

            if (route is not null)
            {
                await EnsureRouteTargetAvailableAsync(connection, transaction, route, cancellationToken);
                await using var insertRoute = connection.CreateCommand();
                insertRoute.Transaction = transaction;
                insertRoute.CommandText =
                    """
                    INSERT INTO caddy_ui.managed_routes(
                        id, name, host, kind, enabled, config_json, created_at, updated_at,
                        domain_id, subdomain, certificate_mode, access_group_id, sort_order)
                    VALUES(
                        @id, @name, @host, @kind, true, CAST(@config_json AS jsonb), @now, @now,
                        @domain_id, @subdomain, @certificate_mode, NULL, 0)
                    """;
                AddParameter(insertRoute, "id", route.Id);
                AddParameter(insertRoute, "name", route.Name);
                AddParameter(insertRoute, "host", route.Host);
                AddParameter(insertRoute, "kind", ManagedRouteDefinition.ToStorageValue(route.Kind));
                AddParameter(insertRoute, "config_json", JsonSerializer.Serialize(route.Configuration, JsonOptions));
                AddParameter(insertRoute, "now", DateTimeOffset.UtcNow);
                AddParameter(insertRoute, "domain_id", route.DomainId);
                AddParameter(insertRoute, "subdomain", route.Subdomain);
                AddParameter(insertRoute, "certificate_mode", ManagedRouteDefinition.ToStorageValue(route.CertificateMode));
                await insertRoute.ExecuteNonQueryAsync(cancellationToken);

                await InsertAuditAsync(
                    connection,
                    transaction,
                    actor,
                    "setup.route.create",
                    "managed_route",
                    route.Id.ToString("D"),
                    JsonSerializer.Serialize(route, JsonOptions),
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new GuidedSetupResult(providerId, domainId, route?.Id, normalizedDomain.Name, route?.Host);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static string NormalizeProviderMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "new" => "new",
            "existing" => "existing",
            _ => "none",
        };
    }

    private static string NormalizeSettings(
        DnsProviderDefinition definition,
        IReadOnlyDictionary<string, string> values)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in definition.Settings)
        {
            var value = values.TryGetValue(field.Key, out var supplied)
                ? supplied?.Trim() ?? string.Empty
                : field.DefaultValue?.Trim() ?? string.Empty;
            if (field.Required && value.Length == 0)
            {
                throw new ArgumentException($"Das Provider-Feld '{field.Label}' ist erforderlich.");
            }

            if (value.Length > 0)
            {
                normalized[field.Key] = value;
            }
        }

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static string NormalizeSecretReferences(
        DnsProviderDefinition definition,
        IReadOnlyDictionary<string, string> values)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in definition.Secrets)
        {
            var value = values.TryGetValue(field.Key, out var supplied)
                ? supplied?.Trim() ?? string.Empty
                : string.Empty;
            if (field.Required && value.Length == 0)
            {
                throw new ArgumentException($"Die Secret-Referenz '{field.Label}' ist erforderlich.");
            }

            if (value.Length == 0)
            {
                continue;
            }

            if (!SecretReferencePattern().IsMatch(value) &&
                !value.StartsWith("secret://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"'{field.Label}' muss der Name einer Umgebungsvariable oder eine secret://-Referenz sein.");
            }

            normalized[field.Key] = value;
        }

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static string BuildUpstream(string? scheme, string? host, int? port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is null or < 1 or > 65_535)
        {
            throw new ArgumentException("Der Upstream-Port muss zwischen 1 und 65535 liegen.");
        }

        var normalizedHost = host.Trim();
        if (normalizedHost.Contains('\r') || normalizedHost.Contains('\n') ||
            normalizedHost.Contains('{') || normalizedHost.Contains('}'))
        {
            throw new ArgumentException("Der Upstream-Host enthält unzulässige Zeichen.");
        }

        if (normalizedHost.Contains(':') &&
            !normalizedHost.StartsWith('[', StringComparison.Ordinal) &&
            !normalizedHost.EndsWith(']', StringComparison.Ordinal))
        {
            normalizedHost = $"[{normalizedHost}]";
        }

        var hostPort = $"{normalizedHost}:{port.Value.ToString(CultureInfo.InvariantCulture)}";
        return scheme?.Trim().ToLowerInvariant() switch
        {
            "http" => $"http://{hostPort}",
            "https" => $"https://{hostPort}",
            _ => hostPort,
        };
    }

    private static async Task EnsureProviderExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid providerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM caddy_ui.dns_providers WHERE id = @id AND enabled";
        AddParameter(command, "id", providerId);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count != 1)
        {
            throw new InvalidOperationException("Der ausgewählte DNS-Provider existiert nicht oder ist deaktiviert.");
        }
    }

    private static async Task InsertProviderAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid providerId,
        DnsProviderDefinition definition,
        string label,
        string settingsJson,
        string secretReferencesJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO caddy_ui.dns_providers(
                id, provider_type, label, enabled, config_json,
                secret_references_json, last_test_status, last_test_error,
                created_at, updated_at)
            VALUES(
                @id, @provider_type, @label, true, CAST(@config_json AS jsonb),
                CAST(@secret_json AS jsonb), 'untested', '', @now, @now)
            """;
        AddParameter(command, "id", providerId);
        AddParameter(command, "provider_type", definition.Type);
        AddParameter(command, "label", label.Trim());
        AddParameter(command, "config_json", settingsJson);
        AddParameter(command, "secret_json", secretReferencesJson);
        AddParameter(command, "now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountDomainsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM caddy_ui.managed_domains";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task EnsureRouteTargetAvailableAsync(
        DbConnection connection,
        DbTransaction transaction,
        ManagedRouteDefinition route,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM caddy_ui.managed_routes
            WHERE enabled
              AND lower(host) = lower(@host)
              AND COALESCE(config_json ->> 'pathPrefix', '/') = @path_prefix
            """;
        AddParameter(command, "host", route.Host);
        AddParameter(command, "path_prefix", route.Configuration.PathPrefix);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count > 0)
        {
            throw new InvalidOperationException($"Eine aktive Route verarbeitet bereits {route.Host}{route.Configuration.PathPrefix}.");
        }
    }

    private static async Task InsertAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        ManagementActor actor,
        string action,
        string objectType,
        string objectId,
        string afterJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO caddy_ui.audit_events(
                occurred_at, actor_user_id, actor_username, remote_address,
                action, object_type, object_id, before_json, after_json,
                result, revision_id, correlation_id)
            VALUES(
                @occurred_at, @actor_user_id, @actor_username,
                NULLIF(@remote_address, '')::inet,
                @action, @object_type, @object_id, '{}'::jsonb, CAST(@after_json AS jsonb),
                'success', NULL, @correlation_id)
            """;
        AddParameter(command, "occurred_at", DateTimeOffset.UtcNow);
        AddParameter(command, "actor_user_id", actor.UserId);
        AddParameter(command, "actor_username", actor.Username.Length <= 200 ? actor.Username : actor.Username[..200]);
        AddParameter(command, "remote_address", actor.RemoteAddress);
        AddParameter(command, "action", action);
        AddParameter(command, "object_type", objectType);
        AddParameter(command, "object_id", objectId);
        AddParameter(command, "after_json", afterJson);
        AddParameter(command, "correlation_id", Guid.NewGuid().ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReferencePattern();
}
