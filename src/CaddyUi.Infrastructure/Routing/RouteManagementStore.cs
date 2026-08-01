using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

    public async Task<IReadOnlyList<AccessGroupRecord>> ListAccessGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT groups.id,
                   groups.name,
                   groups.description,
                   groups.enabled,
                   COUNT(DISTINCT credentials.id)::integer,
                   COUNT(DISTINCT routes.id)::integer,
                   groups.config_json::text,
                   groups.updated_at
            FROM caddy_ui.access_groups AS groups
            LEFT JOIN caddy_ui.access_credentials AS credentials ON credentials.group_id = groups.id
            LEFT JOIN caddy_ui.managed_routes AS routes ON routes.access_group_id = groups.id
            GROUP BY groups.id
            ORDER BY groups.enabled DESC, lower(groups.name)
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AccessGroupRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var presentation = AccessGroupPresentation.FromJson(reader.GetString(6));
            result.Add(new AccessGroupRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                presentation.AccentColor,
                presentation.IconUrl,
                reader.GetBoolean(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                ReadTimestamp(reader, 7)));
        }

        return result;
    }

    public async Task<IReadOnlyList<AccessCredentialRecord>> ListCredentialsAsync(
        Guid? groupId = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, group_id, username, enabled, created_at, updated_at
            FROM caddy_ui.access_credentials
            WHERE (CAST(@group_id AS uuid) IS NULL OR group_id = @group_id)
            ORDER BY lower(username), id
            """;
        AddParameter(command, "group_id", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AccessCredentialRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AccessCredentialRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                ReadTimestamp(reader, 4),
                ReadTimestamp(reader, 5)));
        }

        return result;
    }

    public Task<Guid> CreateAccessGroupAsync(
        string name,
        string description,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        return CreateAccessGroupAsync(
            name,
            description,
            accentColor: null,
            iconUrl: null,
            actor,
            cancellationToken);
    }

    public async Task<Guid> CreateAccessGroupAsync(
        string name,
        string description,
        string? accentColor,
        string? iconUrl,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedName = Required(name, 120, "Group name");
        var normalizedDescription = Limit(description?.Trim() ?? string.Empty, 500);
        var presentation = AccessGroupPresentation.Create(accentColor, iconUrl);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO caddy_ui.access_groups(
                    id, name, config_json, created_at, updated_at, enabled, description)
                VALUES(
                    @id, @name, CAST(@config_json AS jsonb),
                    @now, @now, true, @description)
                """;
            AddParameter(command, "id", id);
            AddParameter(command, "name", normalizedName);
            AddParameter(command, "description", normalizedDescription);
            AddParameter(command, "config_json", presentation.ToJson());
            AddParameter(command, "now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "access-group.create",
                "access_group",
                id.ToString("D"),
                "{}",
                JsonSerializer.Serialize(
                    new
                    {
                        name = normalizedName,
                        description = normalizedDescription,
                        presentation.AccentColor,
                        presentation.IconUrl,
                    },
                    JsonOptions),
                "success",
                null,
                Guid.NewGuid().ToString("N"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task SetAccessGroupEnabledAsync(
        Guid groupId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "UPDATE caddy_ui.access_groups SET enabled = @enabled, updated_at = @now WHERE id = @id",
            command =>
            {
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "id", groupId);
            },
            cancellationToken);
    }

    public async Task<Guid> CreateCredentialAsync(
        Guid groupId,
        string username,
        string passwordHash,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedUsername = Required(username, 120, "Username");
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO caddy_ui.access_credentials(
                    id, group_id, username, password_hash, enabled, created_at, updated_at)
                SELECT @id, groups.id, @username, @password_hash, true, @now, @now
                FROM caddy_ui.access_groups AS groups
                WHERE groups.id = @group_id AND groups.enabled
                """;
            AddParameter(command, "id", id);
            AddParameter(command, "group_id", groupId);
            AddParameter(command, "username", normalizedUsername);
            AddParameter(command, "password_hash", passwordHash);
            AddParameter(command, "now", now);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The selected access group does not exist or is disabled.");
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "access-credential.create",
                "access_credential",
                id.ToString("D"),
                "{}",
                JsonSerializer.Serialize(new { groupId, username = normalizedUsername, enabled = true }, JsonOptions),
                "success",
                null,
                Guid.NewGuid().ToString("N"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task SetCredentialEnabledAsync(
        Guid credentialId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "UPDATE caddy_ui.access_credentials SET enabled = @enabled, updated_at = @now WHERE id = @id",
            command =>
            {
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "id", credentialId);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<CaddyRouteSource>> LoadCompilerSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        var routes = await ListRoutesAsync(cancellationToken);
        return routes
            .Select(route => new CaddyRouteSource(route.Definition, route.AccessGroupName))
            .ToArray();
    }

    public async Task<RouteRevisionRecord> CreateRevisionAsync(
        CaddyCompilation compilation,
        string reason,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(actor);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var normalizedReason = Required(reason, 500, "Revision reason");
        var contentJson = JsonSerializer.Serialize(
            new { format = "caddyfile", content = compilation.Content },
            JsonOptions);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO caddy_ui.route_revisions(
                    id, created_at, actor_user_id, reason, manifest_json, content_json, digest, applied)
                VALUES(
                    @id, @now, @actor_user_id, @reason, CAST(@manifest_json AS jsonb),
                    CAST(@content_json AS jsonb), @digest, false)
                """;
            AddParameter(command, "id", id);
            AddParameter(command, "now", now);
            AddParameter(command, "actor_user_id", actor.UserId);
            AddParameter(command, "reason", normalizedReason);
            AddParameter(command, "manifest_json", compilation.ManifestJson);
            AddParameter(command, "content_json", contentJson);
            AddParameter(command, "digest", compilation.Digest);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "route-revision.create",
                "route_revision",
                id.ToString("D"),
                "{}",
                JsonSerializer.Serialize(new { compilation.Digest, reason = normalizedReason }, JsonOptions),
                "success",
                id,
                Guid.NewGuid().ToString("N"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return new RouteRevisionRecord(
            id,
            now,
            actor.UserId,
            actor.Username,
            normalizedReason,
            compilation.ManifestJson,
            compilation.Content,
            compilation.Digest,
            false);
    }

    public async Task<RouteRevisionRecord?> GetRevisionAsync(
        Guid revisionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revisions.id, revisions.created_at, revisions.actor_user_id,
                   COALESCE(users.username, 'system'), revisions.reason,
                   revisions.manifest_json::text, revisions.content_json::text,
                   revisions.digest, revisions.applied
            FROM caddy_ui.route_revisions AS revisions
            LEFT JOIN caddy_ui.users AS users ON users.id = revisions.actor_user_id
            WHERE revisions.id = @id
            LIMIT 1
            """;
        AddParameter(command, "id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    public async Task<IReadOnlyList<RouteRevisionRecord>> ListRevisionsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revisions.id, revisions.created_at, revisions.actor_user_id,
                   COALESCE(users.username, 'system'), revisions.reason,
                   revisions.manifest_json::text, revisions.content_json::text,
                   revisions.digest, revisions.applied
            FROM caddy_ui.route_revisions AS revisions
            LEFT JOIN caddy_ui.users AS users ON users.id = revisions.actor_user_id
            ORDER BY revisions.created_at DESC
            LIMIT @limit
            """;
        AddParameter(command, "limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RouteRevisionRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRevision(reader));
        }

        return result;
    }

    public async Task<Guid> CreateSnapshotAsync(
        string content,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var contentJson = JsonSerializer.Serialize(new { format = "caddyfile", content }, JsonOptions);
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.caddy_snapshots(id, created_at, digest, manifest_json, content_json, reason)
            VALUES(@id, @now, @digest, '{}'::jsonb, CAST(@content_json AS jsonb), @reason)
            ON CONFLICT (digest) DO NOTHING
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "digest", digest);
                AddParameter(command, "content_json", contentJson);
                AddParameter(command, "reason", Limit(reason, 500));
            },
            cancellationToken);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT id FROM caddy_ui.caddy_snapshots WHERE digest = @digest LIMIT 1";
        AddParameter(lookup, "digest", digest);
        return (Guid)(await lookup.ExecuteScalarAsync(cancellationToken) ?? id);
    }

    public async Task<CaddySnapshotRecord?> GetSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created_at, digest, manifest_json::text, content_json::text, reason
            FROM caddy_ui.caddy_snapshots
            WHERE id = @id
            LIMIT 1
            """;
        AddParameter(command, "id", snapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CaddySnapshotRecord(
            reader.GetGuid(0),
            ReadTimestamp(reader, 1),
            reader.GetString(2),
            reader.GetString(3),
            ReadContent(reader.GetString(4)),
            reader.GetString(5));
    }

    public async Task<Guid> StartOperationAsync(
        Guid? revisionId,
        ManagementActor actor,
        string correlationId,
        Guid? previousSnapshotId,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.apply_operations(
                id, route_revision_id, actor_user_id, correlation_id, state,
                started_at, completed_at, error, previous_snapshot_id)
            VALUES(
                @id, @revision_id, @actor_user_id, @correlation_id, 'running',
                @started_at, NULL, '', @previous_snapshot_id)
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "revision_id", revisionId);
                AddParameter(command, "actor_user_id", actor.UserId);
                AddParameter(command, "correlation_id", correlationId);
                AddParameter(command, "started_at", DateTimeOffset.UtcNow);
                AddParameter(command, "previous_snapshot_id", previousSnapshotId);
            },
            cancellationToken);
        return id;
    }

    public Task RecordOperationStepAsync(
        Guid operationId,
        int sequence,
        string name,
        string state,
        string detailsJson,
        string error,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            INSERT INTO caddy_ui.apply_operation_steps(
                operation_id, sequence, name, state, started_at, completed_at, details_json, error)
            VALUES(
                @operation_id, @sequence, @name, @state, @now, @now,
                CAST(@details_json AS jsonb), @error)
            ON CONFLICT (operation_id, sequence) DO UPDATE SET
                state = EXCLUDED.state,
                completed_at = EXCLUDED.completed_at,
                details_json = EXCLUDED.details_json,
                error = EXCLUDED.error
            """,
            command =>
            {
                AddParameter(command, "operation_id", operationId);
                AddParameter(command, "sequence", sequence);
                AddParameter(command, "name", Limit(name, 120));
                AddParameter(command, "state", Limit(state, 24));
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "details_json", NormalizeObjectJson(detailsJson));
                AddParameter(command, "error", Limit(error, 4000));
            },
            cancellationToken);
    }

    public async Task CompleteOperationAsync(
        Guid operationId,
        Guid? revisionId,
        string state,
        string error,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE caddy_ui.apply_operations
                    SET state = @state, completed_at = @now, error = @error
                    WHERE id = @id
                    """;
                AddParameter(command, "state", Limit(state, 24));
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "error", Limit(error, 4000));
                AddParameter(command, "id", operationId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (revisionId is Guid appliedRevision && state == "applied")
            {
                await using var revision = connection.CreateCommand();
                revision.Transaction = transaction;
                revision.CommandText =
                    "UPDATE caddy_ui.route_revisions SET applied = (id = @id) WHERE applied OR id = @id";
                AddParameter(revision, "id", appliedRevision);
                await revision.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "caddy.apply",
                "apply_operation",
                operationId.ToString("D"),
                "{}",
                JsonSerializer.Serialize(new { state, error = Limit(error, 4000) }, JsonOptions),
                state,
                revisionId,
                operationId.ToString("N"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ApplyOperationRecord>> ListOperationsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, route_revision_id, correlation_id, state, started_at,
                   completed_at, error, previous_snapshot_id
            FROM caddy_ui.apply_operations
            ORDER BY started_at DESC
            LIMIT @limit
            """;
        AddParameter(command, "limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ApplyOperationRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ApplyOperationRecord(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadTimestamp(reader, 4),
                reader.IsDBNull(5) ? null : ReadTimestamp(reader, 5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7)));
        }

        return result;
    }

    public async Task<ApplyOperationRecord?> GetLatestAppliedOperationAsync(
        CancellationToken cancellationToken = default)
    {
        var operations = await ListOperationsAsync(100, cancellationToken);
        return operations.FirstOrDefault(operation =>
            operation.State == "applied" && operation.PreviousSnapshotId is not null);
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

    private static RouteRevisionRecord ReadRevision(DbDataReader reader)
    {
        return new RouteRevisionRecord(
            reader.GetGuid(0),
            ReadTimestamp(reader, 1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ReadContent(reader.GetString(6)),
            reader.GetString(7),
            reader.GetBoolean(8));
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

    private static string ReadContent(string contentJson)
    {
        using var document = JsonDocument.Parse(contentJson);
        return document.RootElement.TryGetProperty("content", out var content)
            ? content.GetString() ?? string.Empty
            : string.Empty;
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

    private async Task ExecuteAsync(
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
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

    private static string Required(string? value, int maximum, string description)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            throw new ArgumentException($"{description} is required.", nameof(value));
        }

        return Limit(candidate, maximum);
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
