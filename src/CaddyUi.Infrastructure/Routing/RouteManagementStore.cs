using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Routing;

public sealed class RouteManagementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public RouteManagementStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<ManagedDomainOption>> ListDomainsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, display_name, enabled, is_default
            FROM caddy_ui.managed_domains
            ORDER BY is_default DESC, lower(name)
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ManagedDomainOption>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ManagedDomainOption(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        return result;
    }

    public async Task<IReadOnlyList<ManagedRouteRecord>> ListRoutesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RouteSelectSql +
            " ORDER BY routes.enabled DESC, lower(routes.host), routes.sort_order, lower(routes.name)";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ManagedRouteRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRoute(reader));
        }

        return result;
    }

    public async Task<ManagedRouteRecord?> GetRouteAsync(
        Guid routeId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RouteSelectSql + " WHERE routes.id = @id LIMIT 1";
        AddParameter(command, "id", routeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRoute(reader) : null;
    }

    public async Task<Guid> CreateRouteAsync(
        ManagedRouteDefinition route,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(actor);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureRouteTargetAvailableAsync(connection, transaction, route, cancellationToken);
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
            BindRoute(command, route, now);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "route.create",
                "managed_route",
                route.Id.ToString("D"),
                "{}",
                SerializeRoute(route),
                "success",
                null,
                Guid.NewGuid().ToString("N"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return route.Id;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task UpdateRouteAsync(
        ManagedRouteDefinition route,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(actor);
        var previous = await GetRouteAsync(route.Id, cancellationToken) ??
            throw new InvalidOperationException("The selected route no longer exists.");
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureRouteTargetAvailableAsync(connection, transaction, route, cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE caddy_ui.managed_routes
                SET name = @name,
                    host = @host,
                    kind = @kind,
                    enabled = @enabled,
                    config_json = CAST(@config_json AS jsonb),
                    updated_at = @now,
                    domain_id = @domain_id,
                    subdomain = @subdomain,
                    certificate_mode = @certificate_mode,
                    access_group_id = @access_group_id,
                    sort_order = @sort_order
                WHERE id = @id
                """;
            BindRoute(command, route, DateTimeOffset.UtcNow);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The selected route no longer exists.");
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "route.update",
                "managed_route",
                route.Id.ToString("D"),
                SerializeRoute(previous.Definition),
                SerializeRoute(route),
                "success",
                null,
                Guid.NewGuid().ToString("N"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteRouteAsync(
        Guid routeId,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var previous = await GetRouteAsync(routeId, cancellationToken) ??
            throw new InvalidOperationException("The selected route no longer exists.");
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM caddy_ui.managed_routes WHERE id = @id";
            AddParameter(command, "id", routeId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The selected route no longer exists.");
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "route.delete",
                "managed_route",
                routeId.ToString("D"),
                SerializeRoute(previous.Definition),
                "{}",
                "success",
                null,
                Guid.NewGuid().ToString("N"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<CaddyRouteSource>> LoadCompilerSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        var routes = await ListRoutesAsync(cancellationToken);
        return routes
            .Select(route => new CaddyRouteSource(route.Definition, route.AccessGroupName))
            .ToArray();
    }

    private static ManagedRouteRecord ReadRoute(DbDataReader reader)
    {
        var configuration = DeserializeConfiguration(reader.GetString(11));
        var definition = ManagedRouteDefinition.Create(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(6),
            reader.GetString(7),
            reader.GetString(8),
            ManagedRouteDefinition.ParseKind(reader.GetString(3)),
            reader.GetBoolean(4),
            reader.GetInt32(10),
            ManagedRouteDefinition.ParseCertificateMode(reader.GetString(9)),
            reader.IsDBNull(12) ? null : reader.GetGuid(12),
            configuration);
        return new ManagedRouteRecord(
            definition,
            reader.GetString(13),
            reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            ReadTimestamp(reader, 5),
            ReadTimestamp(reader, 15));
    }

    private static RouteConfigurationDocument DeserializeConfiguration(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<RouteConfigurationDocument>(value, JsonOptions) ??
                RouteConfigurationDocument.Empty;
        }
        catch (JsonException)
        {
            return RouteConfigurationDocument.Empty;
        }
    }

    private static async Task EnsureRouteTargetAvailableAsync(
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
            SELECT COUNT(*)
            FROM caddy_ui.managed_routes
            WHERE id <> @id
              AND enabled
              AND lower(host) = lower(@host)
              AND COALESCE(config_json ->> 'pathPrefix', '/') = @path_prefix
            """;
        AddParameter(command, "id", route.Id);
        AddParameter(command, "host", route.Host);
        AddParameter(command, "path_prefix", route.Configuration.PathPrefix);
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (count > 0)
        {
            throw new InvalidOperationException(
                $"An enabled route already handles {route.Host}{route.Configuration.PathPrefix}.");
        }
    }

    private static void BindRoute(
        DbCommand command,
        ManagedRouteDefinition route,
        DateTimeOffset now)
    {
        AddParameter(command, "id", route.Id);
        AddParameter(command, "name", route.Name);
        AddParameter(command, "host", route.Host);
        AddParameter(command, "kind", ManagedRouteDefinition.ToStorageValue(route.Kind));
        AddParameter(command, "enabled", route.Enabled);
        AddParameter(command, "config_json", JsonSerializer.Serialize(route.Configuration, JsonOptions));
        AddParameter(command, "now", now);
        AddParameter(command, "domain_id", route.DomainId);
        AddParameter(command, "subdomain", route.Subdomain);
        AddParameter(command, "certificate_mode", ManagedRouteDefinition.ToStorageValue(route.CertificateMode));
        AddParameter(command, "access_group_id", route.AccessGroupId);
        AddParameter(command, "sort_order", route.SortOrder);
    }

    private static string SerializeRoute(ManagedRouteDefinition route)
    {
        return JsonSerializer.Serialize(route, JsonOptions);
    }

    private static async Task InsertAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        ManagementActor actor,
        string action,
        string objectType,
        string objectId,
        string beforeJson,
        string afterJson,
        string result,
        Guid? revisionId,
        string correlationId,
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
                @action, @object_type, @object_id,
                CAST(@before_json AS jsonb), CAST(@after_json AS jsonb),
                @result, @revision_id, @correlation_id)
            """;
        AddParameter(command, "occurred_at", DateTimeOffset.UtcNow);
        AddParameter(command, "actor_user_id", actor.UserId);
        AddParameter(command, "actor_username", Limit(actor.Username, 200));
        AddParameter(command, "remote_address", actor.RemoteAddress);
        AddParameter(command, "action", action);
        AddParameter(command, "object_type", objectType);
        AddParameter(command, "object_id", objectId);
        AddParameter(command, "before_json", NormalizeObjectJson(beforeJson));
        AddParameter(command, "after_json", NormalizeObjectJson(afterJson));
        AddParameter(command, "result", result);
        AddParameter(command, "revision_id", revisionId);
        AddParameter(command, "correlation_id", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeObjectJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.GetRawText()
                : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static string Limit(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
    }

    private static async Task<DbConnection> OpenConnectionAsync(
        CaddyUiDbContext context,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return connection;
    }

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset timestamp => timestamp,
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                CultureInfo.InvariantCulture),
        };
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private const string RouteSelectSql =
        """
        SELECT routes.id,
               routes.name,
               routes.host,
               routes.kind,
               routes.enabled,
               routes.created_at,
               routes.domain_id,
               domains.name,
               routes.subdomain,
               routes.certificate_mode,
               routes.sort_order,
               routes.config_json::text,
               routes.access_group_id,
               domains.display_name,
               groups.name,
               routes.updated_at
        FROM caddy_ui.managed_routes AS routes
        JOIN caddy_ui.managed_domains AS domains ON domains.id = routes.domain_id
        LEFT JOIN caddy_ui.access_groups AS groups ON groups.id = routes.access_group_id
        """;
}
