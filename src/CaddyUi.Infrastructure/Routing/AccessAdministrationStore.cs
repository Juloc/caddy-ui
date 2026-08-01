using System.Data;
using System.Data.Common;
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

    public async Task UpdateGroupAsync(
        Guid groupId,
        string name,
        string description,
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
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE caddy_ui.access_groups
                SET name = @name,
                    description = @description,
                    updated_at = @now
                WHERE id = @id
                """;
            AddParameter(command, "name", normalizedName);
            AddParameter(command, "description", normalizedDescription);
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
                    password_hash = CASE
                        WHEN @password_hash IS NULL THEN password_hash
                        ELSE @password_hash
                    END,
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
                reader.GetInt32(3),
                reader.GetInt32(4))
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
                @occurred_at, @actor_user_id, @actor_username, @remote_address,
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
        int CredentialCount,
        int RouteCount);

    private sealed record CredentialState(
        Guid GroupId,
        string Username,
        bool Enabled);
}
