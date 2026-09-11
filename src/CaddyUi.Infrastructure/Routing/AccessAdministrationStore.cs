using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Routing;

public sealed class AccessAdministrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public AccessAdministrationStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
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

    public Task UpdateGroupAsync(
        Guid groupId,
        string name,
        string description,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        return UpdateGroupAsync(
            groupId,
            name,
            description,
            accentColor: null,
            iconUrl: null,
            actor,
            cancellationToken);
    }

    public async Task UpdateGroupAsync(
        Guid groupId,
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
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var previous = await ReadGroupAsync(connection, transaction, groupId, cancellationToken) ??
                throw new InvalidOperationException("The selected access group no longer exists.");
            var presentation = accentColor is null && iconUrl is null
                ? AccessGroupPresentation.FromJson(previous.ConfigJson)
                : AccessGroupPresentation.Create(accentColor, iconUrl);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE caddy_ui.access_groups
                SET name = @name,
                    description = @description,
                    config_json = CAST(@config_json AS jsonb),
                    updated_at = @now
                WHERE id = @id
                """;
            AddParameter(command, "name", normalizedName);
            AddParameter(command, "description", normalizedDescription);
            AddParameter(command, "config_json", presentation.ToJson());
            AddParameter(command, "now", DateTimeOffset.UtcNow);
            AddParameter(command, "id", groupId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The selected access group no longer exists.");
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "access-group.update",
                "access_group",
                groupId.ToString("D"),
                JsonSerializer.Serialize(previous, JsonOptions),
                JsonSerializer.Serialize(
                    new
                    {
                        name = normalizedName,
                        description = normalizedDescription,
                        presentation.AccentColor,
                        presentation.IconUrl,
                        previous.Enabled,
                    },
                    JsonOptions),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteGroupAsync(
        Guid groupId,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var previous = await ReadGroupAsync(connection, transaction, groupId, cancellationToken) ??
                throw new InvalidOperationException("The selected access group no longer exists.");
            if (previous.RouteCount > 0 || previous.CredentialCount > 0)
            {
                throw new InvalidOperationException(
                    "Remove all assigned routes and portal credentials before deleting this access group.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM caddy_ui.access_groups WHERE id = @id";
            AddParameter(command, "id", groupId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The selected access group no longer exists.");
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "access-group.delete",
                "access_group",
                groupId.ToString("D"),
                JsonSerializer.Serialize(previous, JsonOptions),
                "{}",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
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

    public async Task UpdateCredentialAsync(
        Guid credentialId,
        string username,
        string? passwordHash,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedUsername = Required(username, 120, "Username");
        var normalizedPasswordHash = string.IsNullOrWhiteSpace(passwordHash)
            ? null
            : passwordHash.Trim();
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var previous = await ReadCredentialAsync(
                connection,
                transaction,
                credentialId,
                cancellationToken) ?? throw new InvalidOperationException(
                "The selected portal credential no longer exists.");
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE caddy_ui.access_credentials
                SET username = @username,
                    password_hash = COALESCE(CAST(@password_hash AS text), password_hash),
                    updated_at = @now
                WHERE id = @id
                """;
            AddParameter(command, "username", normalizedUsername);
            AddParameter(command, "password_hash", normalizedPasswordHash);
            AddParameter(command, "now", DateTimeOffset.UtcNow);
            AddParameter(command, "id", credentialId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The selected portal credential no longer exists.");
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "access-credential.update",
                "access_credential",
                credentialId.ToString("D"),
                JsonSerializer.Serialize(previous, JsonOptions),
                JsonSerializer.Serialize(
                    new
                    {
                        previous.GroupId,
                        username = normalizedUsername,
                        previous.Enabled,
                        passwordChanged = normalizedPasswordHash is not null,
                    },
                    JsonOptions),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteCredentialAsync(
        Guid credentialId,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var previous = await ReadCredentialAsync(
                connection,
                transaction,
                credentialId,
                cancellationToken) ?? throw new InvalidOperationException(
                "The selected portal credential no longer exists.");
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM caddy_ui.access_credentials WHERE id = @id";
            AddParameter(command, "id", credentialId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("The selected portal credential no longer exists.");
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "access-credential.delete",
                "access_credential",
                credentialId.ToString("D"),
                JsonSerializer.Serialize(previous, JsonOptions),
                "{}",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<GroupState?> ReadGroupAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT groups.name,
                   groups.description,
                   groups.enabled,
                   groups.config_json::text,
                   (SELECT COUNT(*)::integer
                    FROM caddy_ui.access_credentials AS credentials
                    WHERE credentials.group_id = groups.id),
                   (SELECT COUNT(*)::integer
                    FROM caddy_ui.managed_routes AS routes
                    WHERE routes.access_group_id = groups.id)
            FROM caddy_ui.access_groups AS groups
            WHERE groups.id = @id
            FOR UPDATE
            """;
        AddParameter(command, "id", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GroupState(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5))
            : null;
    }

    private static async Task<CredentialState?> ReadCredentialAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT group_id, username, enabled
            FROM caddy_ui.access_credentials
            WHERE id = @id
            FOR UPDATE
            """;
        AddParameter(command, "id", credentialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CredentialState(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetBoolean(2))
            : null;
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
                'success', NULL, @correlation_id)
            """;
        AddParameter(command, "occurred_at", DateTimeOffset.UtcNow);
        AddParameter(command, "actor_user_id", actor.UserId);
        AddParameter(command, "actor_username", Limit(actor.Username, 200));
        AddParameter(command, "remote_address", actor.RemoteAddress);
        AddParameter(command, "action", action);
        AddParameter(command, "object_type", objectType);
        AddParameter(command, "object_id", objectId);
        AddParameter(command, "before_json", beforeJson);
        AddParameter(command, "after_json", afterJson);
        AddParameter(command, "correlation_id", Guid.NewGuid().ToString("N"));
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
        parameter.ParameterName = $"@{name}";
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Required(string? value, int maximum, string label)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException($"{label} is required.");
        }

        return Limit(normalized, maximum);
    }

    private static string Limit(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
    }

    private sealed record GroupState(
        string Name,
        string Description,
        bool Enabled,
        string ConfigJson,
        int CredentialCount,
        int RouteCount);

    private sealed record CredentialState(
        Guid GroupId,
        string Username,
        bool Enabled);
}
