using System.Data;
using System.Data.Common;
using System.Text.Json;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Routing;

public sealed record RouteTransferDocument(
    string Schema,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<RouteTransferItem> Routes);

public sealed record RouteTransferItem(
    string Name,
    string Domain,
    string Subdomain,
    string Kind,
    bool Enabled,
    int SortOrder,
    string CertificateMode,
    string AccessGroup,
    RouteConfigurationDocument Configuration);

public sealed record RouteImportResult(int ImportedRoutes, IReadOnlyList<string> Hosts);

public sealed class RouteTransferService
{
    private const string Schema = "caddy-ui-routes-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly RouteManagementStore _routeStore;
    private readonly RouteImportStore _importStore;
    private readonly RoutingOptions _options;

    public RouteTransferService(
        RouteManagementStore routeStore,
        RouteImportStore importStore,
        RoutingOptions options)
    {
        _routeStore = routeStore;
        _importStore = importStore;
        _options = options;
    }

    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        var routes = await _routeStore.ListRoutesAsync(cancellationToken);
        var document = new RouteTransferDocument(
            Schema,
            DateTimeOffset.UtcNow,
            routes.Select(route => new RouteTransferItem(
                route.Definition.Name,
                route.Definition.DomainName,
                route.Definition.Subdomain,
                ManagedRouteDefinition.ToStorageValue(route.Definition.Kind),
                route.Definition.Enabled,
                route.Definition.SortOrder,
                ManagedRouteDefinition.ToStorageValue(route.Definition.CertificateMode),
                route.AccessGroupName,
                route.Definition.Configuration)).ToArray());
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public async Task<RouteImportResult> ImportAsync(
        string json,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Die Importdatei ist leer.", nameof(json));
        }

        RouteTransferDocument document;
        try
        {
            document = JsonSerializer.Deserialize<RouteTransferDocument>(json, JsonOptions) ??
                throw new ArgumentException("Die Importdatei enthält kein Dokument.", nameof(json));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Die Importdatei ist kein gültiges JSON-Dokument.",
                nameof(json),
                exception);
        }

        if (!string.Equals(document.Schema, Schema, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Nicht unterstütztes Importschema '{document.Schema}'. Erwartet wird '{Schema}'.",
                nameof(json));
        }

        if (document.Routes is null || document.Routes.Count is < 1 or > 500)
        {
            throw new ArgumentException(
                "Ein Import muss zwischen 1 und 500 Routen enthalten.",
                nameof(json));
        }

        var domains = await _routeStore.ListDomainsAsync(cancellationToken);
        var groups = await _routeStore.ListAccessGroupsAsync(cancellationToken);
        var definitions = new List<ManagedRouteDefinition>(document.Routes.Count);
        foreach (var item in document.Routes)
        {
            var domain = domains.FirstOrDefault(candidate =>
                candidate.Enabled &&
                string.Equals(candidate.Name, item.Domain?.Trim(), StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidOperationException(
                    $"Die Domain '{item.Domain}' ist nicht vorhanden oder deaktiviert.");
            Guid? accessGroupId = null;
            if (!string.IsNullOrWhiteSpace(item.AccessGroup))
            {
                var group = groups.FirstOrDefault(candidate =>
                    candidate.Enabled &&
                    string.Equals(candidate.Name, item.AccessGroup.Trim(), StringComparison.OrdinalIgnoreCase)) ??
                    throw new InvalidOperationException(
                        $"Die Zugriffsgruppe '{item.AccessGroup}' ist nicht vorhanden oder deaktiviert.");
                accessGroupId = group.Id;
            }

            var kind = ManagedRouteDefinition.ParseKind(item.Kind);
            if (kind == ManagedRouteKind.Custom && !_options.AllowCustomRoutes)
            {
                throw new InvalidOperationException(
                    $"Die Route '{item.Name}' ist eine Custom Route, Custom Routes sind jedoch deaktiviert.");
            }

            definitions.Add(ManagedRouteDefinition.Create(
                Guid.NewGuid(),
                item.Name,
                domain.Id,
                domain.Name,
                item.Subdomain,
                kind,
                item.Enabled,
                item.SortOrder,
                ManagedRouteDefinition.ParseCertificateMode(item.CertificateMode),
                accessGroupId,
                item.Configuration));
        }

        var duplicate = definitions
            .Where(route => route.Enabled)
            .GroupBy(route => (route.Host, route.Configuration.PathPrefix))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Der Import enthält das aktive Routenziel {duplicate.Key.Host}{duplicate.Key.PathPrefix} mehrfach.");
        }

        await _importStore.ImportAsync(definitions, actor, cancellationToken);
        return new RouteImportResult(
            definitions.Count,
            definitions
                .Select(route => route.Host)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }
}

public sealed class RouteImportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public RouteImportStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task ImportAsync(
        IReadOnlyCollection<ManagedRouteDefinition> routes,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(actor);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            foreach (var route in routes)
            {
                await ValidateReferencesAsync(connection, transaction, route, cancellationToken);
                await EnsureTargetAvailableAsync(connection, transaction, route, cancellationToken);
                await InsertRouteAsync(connection, transaction, route, cancellationToken);
            }

            await InsertAuditAsync(connection, transaction, routes, actor, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task ValidateReferencesAsync(
        DbConnection connection,
        DbTransaction transaction,
        ManagedRouteDefinition route,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM caddy_ui.managed_domains
                WHERE id = @domain_id AND enabled)
              AND (CAST(@access_group_id AS uuid) IS NULL OR EXISTS(
                SELECT 1
                FROM caddy_ui.access_groups
                WHERE id = CAST(@access_group_id AS uuid) AND enabled))
            """;
        AddParameter(command, "domain_id", route.DomainId);
        AddParameter(command, "access_group_id", route.AccessGroupId);
        var valid = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
        if (!valid)
        {
            throw new InvalidOperationException(
                $"Die Referenzen der Route '{route.Name}' wurden während des Imports geändert.");
        }
    }

    private static async Task EnsureTargetAvailableAsync(
        DbConnection connection,
        DbTransaction transaction,
        ManagedRouteDefinition route,
        CancellationToken cancellationToken)
    {
        if (!route.Enabled)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM caddy_ui.managed_routes
                WHERE enabled
                  AND lower(host) = lower(@host)
                  AND COALESCE(config_json ->> 'pathPrefix', '/') = @path_prefix)
            """;
        AddParameter(command, "host", route.Host);
        AddParameter(command, "path_prefix", route.Configuration.PathPrefix);
        if (Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)))
        {
            throw new InvalidOperationException(
                $"Eine aktive Route für {route.Host}{route.Configuration.PathPrefix} existiert bereits.");
        }
    }

    private static async Task InsertRouteAsync(
        DbConnection connection,
        DbTransaction transaction,
        ManagedRouteDefinition route,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO caddy_ui.managed_routes(
                id, name, host, kind, enabled, config_json, created_at, updated_at,
                domain_id, subdomain, certificate_mode, access_group_id, sort_order)
            VALUES(
                @id, @name, @host, @kind, @enabled, CAST(@config_json AS jsonb), @now, @now,
                @domain_id, @subdomain, @certificate_mode, @access_group_id, @sort_order)
            """;
        AddParameter(command, "id", route.Id);
        AddParameter(command, "name", route.Name);
        AddParameter(command, "host", route.Host);
        AddParameter(command, "kind", ManagedRouteDefinition.ToStorageValue(route.Kind));
        AddParameter(command, "enabled", route.Enabled);
        AddParameter(
            command,
            "config_json",
            JsonSerializer.Serialize(route.Configuration, JsonOptions));
        AddParameter(command, "now", now);
        AddParameter(command, "domain_id", route.DomainId);
        AddParameter(command, "subdomain", route.Subdomain);
        AddParameter(
            command,
            "certificate_mode",
            ManagedRouteDefinition.ToStorageValue(route.CertificateMode));
        AddParameter(command, "access_group_id", route.AccessGroupId);
        AddParameter(command, "sort_order", route.SortOrder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<ManagedRouteDefinition> routes,
        ManagementActor actor,
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
                'route.import', 'managed_route_collection', @object_id,
                '{}'::jsonb, CAST(@after_json AS jsonb),
                'success', NULL, @correlation_id)
            """;
        var correlationId = Guid.NewGuid().ToString("N");
        AddParameter(command, "occurred_at", DateTimeOffset.UtcNow);
        AddParameter(command, "actor_user_id", actor.UserId);
        AddParameter(command, "actor_username", actor.Username);
        AddParameter(command, "remote_address", actor.RemoteAddress);
        AddParameter(command, "object_id", correlationId);
        AddParameter(
            command,
            "after_json",
            JsonSerializer.Serialize(
                new
                {
                    count = routes.Count,
                    routeIds = routes.Select(route => route.Id),
                    hosts = routes
                        .Select(route => route.Host)
                        .Distinct(StringComparer.Ordinal),
                },
                JsonOptions));
        AddParameter(command, "correlation_id", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
