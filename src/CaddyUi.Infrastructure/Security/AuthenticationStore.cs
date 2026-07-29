using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Security;

public sealed class AuthenticationStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public AuthenticationStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<int> CountUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM caddy_ui.users";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public async Task CreateUserAsync(
        string username,
        string displayName,
        string passwordHash,
        string role,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO caddy_ui.users(
                id, username, display_name, password_hash, role, enabled,
                totp_secret_encrypted, totp_enabled, theme, created_at, updated_at)
            VALUES(
                @id, @username, @display_name, @password_hash, @role, true,
                NULL, false, 'system', @now, @now)
            """;
        AddParameter(command, "id", Guid.NewGuid());
        AddParameter(command, "username", username.Trim());
        AddParameter(command, "display_name", displayName.Trim());
        AddParameter(command, "password_hash", passwordHash);
        AddParameter(command, "role", role);
        AddParameter(command, "now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<UserAccount?> FindUserByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, username, display_name, password_hash, role, enabled,
                   totp_secret_encrypted, totp_enabled, theme
            FROM caddy_ui.users
            WHERE lower(username) = lower(@username)
            LIMIT 1
            """;
        AddParameter(command, "username", username.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadUser(reader)
            : null;
    }

    public async Task UpdatePasswordHashAsync(
        Guid userId,
        string passwordHash,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            """
            UPDATE caddy_ui.users
            SET password_hash = @password_hash, updated_at = @now
            WHERE id = @user_id
            """,
            command =>
            {
                AddParameter(command, "password_hash", passwordHash);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "user_id", userId);
            },
            cancellationToken);
    }

    public async Task SetTotpAsync(
        Guid userId,
        byte[]? encryptedSecret,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            """
            UPDATE caddy_ui.users
            SET totp_secret_encrypted = @secret,
                totp_enabled = @enabled,
                updated_at = @now
            WHERE id = @user_id
            """,
            command =>
            {
                AddParameter(command, "secret", encryptedSecret);
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "user_id", userId);
            },
            cancellationToken);
    }

    public async Task ReplaceRecoveryCodesAsync(
        Guid userId,
        IReadOnlyCollection<string> codeHashes,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM caddy_ui.user_recovery_codes WHERE user_id = @user_id";
                AddParameter(delete, "user_id", userId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var codeHash in codeHashes)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO caddy_ui.user_recovery_codes(user_id, code_hash, created_at)
                    VALUES(@user_id, @code_hash, @created_at)
                    """;
                AddParameter(insert, "user_id", userId);
                AddParameter(insert, "code_hash", codeHash);
                AddParameter(insert, "created_at", DateTimeOffset.UtcNow);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> ConsumeRecoveryCodeAsync(
        Guid userId,
        string codeHash,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE caddy_ui.user_recovery_codes
            SET used_at = @used_at
            WHERE user_id = @user_id
              AND code_hash = @code_hash
              AND used_at IS NULL
            """;
        AddParameter(command, "used_at", DateTimeOffset.UtcNow);
        AddParameter(command, "user_id", userId);
        AddParameter(command, "code_hash", codeHash);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<string> CreateAdminSessionAsync(
        Guid userId,
        TimeSpan lifetime,
        string remoteAddress,
        string userAgent,
        CancellationToken cancellationToken = default)
    {
        var token = CreateToken();
        var now = DateTimeOffset.UtcNow;

        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.admin_sessions(
                token_hash, user_id, csrf_token_hash, created_at, expires_at,
                remote_address, user_agent, last_seen_at)
            VALUES(
                @token_hash, @user_id, @csrf_token_hash, @created_at, @expires_at,
                NULLIF(@remote_address, '')::inet, @user_agent, @last_seen_at)
            """,
            command =>
            {
                AddParameter(command, "token_hash", HashToken(token));
                AddParameter(command, "user_id", userId);
                AddParameter(command, "csrf_token_hash", HashToken(CreateToken()));
                AddParameter(command, "created_at", now);
                AddParameter(command, "expires_at", now.Add(lifetime));
                AddParameter(command, "remote_address", remoteAddress);
                AddParameter(command, "user_agent", Limit(userAgent, 400));
                AddParameter(command, "last_seen_at", now);
            },
            cancellationToken);

        return token;
    }

    public async Task<ValidatedAdminSession?> ValidateAdminSessionAsync(
        string token,
        string userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        var tokenHash = HashToken(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session.expires_at, session.user_agent,
                   users.id, users.username, users.display_name, users.password_hash,
                   users.role, users.enabled, users.totp_secret_encrypted,
                   users.totp_enabled, users.theme
            FROM caddy_ui.admin_sessions AS session
            JOIN caddy_ui.users AS users ON users.id = session.user_id
            WHERE session.token_hash = @token_hash
              AND session.expires_at > @now
              AND users.enabled
            LIMIT 1
            """;
        AddParameter(command, "token_hash", tokenHash);
        AddParameter(command, "now", DateTimeOffset.UtcNow);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedAgent = reader.GetString(1);
        if (!string.Equals(storedAgent, Limit(userAgent, 400), StringComparison.Ordinal))
        {
            return null;
        }

        var expiresAt = ReadTimestamp(reader, 0);
        var user = ReadUser(reader, offset: 2);
        await reader.DisposeAsync();

        await using var touch = connection.CreateCommand();
        touch.CommandText =
            "UPDATE caddy_ui.admin_sessions SET last_seen_at = @now WHERE token_hash = @token_hash";
        AddParameter(touch, "now", DateTimeOffset.UtcNow);
        AddParameter(touch, "token_hash", tokenHash);
        await touch.ExecuteNonQueryAsync(cancellationToken);

        return new ValidatedAdminSession(user, tokenHash, expiresAt);
    }

    public Task RevokeAdminSessionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "DELETE FROM caddy_ui.admin_sessions WHERE token_hash = @token_hash",
            command => AddParameter(command, "token_hash", HashToken(token)),
            cancellationToken);
    }

    public async Task RecordLoginAttemptAsync(
        string scope,
        string identity,
        string remoteAddress,
        bool succeeded,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.login_attempts(
                occurred_at, scope, identity, remote_address, succeeded, reason)
            VALUES(
                @occurred_at, @scope, @identity,
                NULLIF(@remote_address, '')::inet, @succeeded, @reason)
            """,
            command =>
            {
                AddParameter(command, "occurred_at", DateTimeOffset.UtcNow);
                AddParameter(command, "scope", scope);
                AddParameter(command, "identity", identity.Trim().ToLowerInvariant());
                AddParameter(command, "remote_address", remoteAddress);
                AddParameter(command, "succeeded", succeeded);
                AddParameter(command, "reason", reason);
            },
            cancellationToken);
    }

    public async Task<int> CountRecentFailuresAsync(
        string scope,
        string identity,
        string remoteAddress,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM caddy_ui.login_attempts
            WHERE scope = @scope
              AND occurred_at >= @since
              AND NOT succeeded
              AND (
                    lower(identity) = lower(@identity)
                    OR remote_address = NULLIF(@remote_address, '')::inet
                  )
            """;
        AddParameter(command, "scope", scope);
        AddParameter(command, "since", since);
        AddParameter(command, "identity", identity.Trim());
        AddParameter(command, "remote_address", remoteAddress);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public async Task<ActiveLoginBlock?> GetActiveLoginBlockAsync(
        string scope,
        string identity,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT expires_at, reason
            FROM caddy_ui.login_blocks
            WHERE scope = @scope
              AND released_at IS NULL
              AND expires_at > @now
              AND (
                    lower(identity) = lower(@identity)
                    OR remote_address = NULLIF(@remote_address, '')::inet
                  )
            ORDER BY expires_at DESC
            LIMIT 1
            """;
        AddParameter(command, "scope", scope);
        AddParameter(command, "now", DateTimeOffset.UtcNow);
        AddParameter(command, "identity", identity.Trim());
        AddParameter(command, "remote_address", remoteAddress);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ActiveLoginBlock(ReadTimestamp(reader, 0), reader.GetString(1))
            : null;
    }

    public Task AddLoginBlockAsync(
        string scope,
        string identity,
        string remoteAddress,
        string reason,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            INSERT INTO caddy_ui.login_blocks(
                id, scope, identity, remote_address, reason, created_at, expires_at)
            VALUES(
                @id, @scope, @identity, NULLIF(@remote_address, '')::inet,
                @reason, @created_at, @expires_at)
            """,
            command =>
            {
                AddParameter(command, "id", Guid.NewGuid());
                AddParameter(command, "scope", scope);
                AddParameter(command, "identity", identity.Trim().ToLowerInvariant());
                AddParameter(command, "remote_address", remoteAddress);
                AddParameter(command, "reason", reason);
                AddParameter(command, "created_at", DateTimeOffset.UtcNow);
                AddParameter(command, "expires_at", expiresAt);
            },
            cancellationToken);
    }

    public Task ClearLoginBlocksAsync(
        string scope,
        string identity,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            UPDATE caddy_ui.login_blocks
            SET released_at = @released_at
            WHERE scope = @scope
              AND released_at IS NULL
              AND (
                    lower(identity) = lower(@identity)
                    OR remote_address = NULLIF(@remote_address, '')::inet
                  )
            """,
            command =>
            {
                AddParameter(command, "released_at", DateTimeOffset.UtcNow);
                AddParameter(command, "scope", scope);
                AddParameter(command, "identity", identity.Trim());
                AddParameter(command, "remote_address", remoteAddress);
            },
            cancellationToken);
    }

    public async Task<PortalAccessGroup?> FindAccessGroupAsync(
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, config_json::text FROM caddy_ui.access_groups WHERE id = @id";
        AddParameter(command, "id", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PortalAccessGroup(reader.GetGuid(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    public async Task<PortalCredential?> FindPortalCredentialAsync(
        Guid groupId,
        string username,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, group_id, username, password_hash, enabled
            FROM caddy_ui.access_credentials
            WHERE group_id = @group_id
              AND lower(username) = lower(@username)
            LIMIT 1
            """;
        AddParameter(command, "group_id", groupId);
        AddParameter(command, "username", username.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PortalCredential(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4))
            : null;
    }

    public async Task<string> CreatePortalSessionAsync(
        PortalCredential credential,
        TimeSpan lifetime,
        string remoteAddress,
        string userAgent,
        CancellationToken cancellationToken = default)
    {
        var token = CreateToken();
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.portal_sessions(
                token_hash, credential_id, group_id, created_at, expires_at,
                user_agent, remote_address)
            VALUES(
                @token_hash, @credential_id, @group_id, @created_at, @expires_at,
                @user_agent, NULLIF(@remote_address, '')::inet)
            """,
            command =>
            {
                AddParameter(command, "token_hash", HashToken(token));
                AddParameter(command, "credential_id", credential.Id);
                AddParameter(command, "group_id", credential.GroupId);
                AddParameter(command, "created_at", now);
                AddParameter(command, "expires_at", now.Add(lifetime));
                AddParameter(command, "user_agent", Limit(userAgent, 400));
                AddParameter(command, "remote_address", remoteAddress);
            },
            cancellationToken);
        return token;
    }

    public async Task<ValidatedPortalSession?> ValidatePortalSessionAsync(
        Guid groupId,
        string token,
        string userAgent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        var tokenHash = HashToken(token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT credentials.username, sessions.expires_at, sessions.user_agent
            FROM caddy_ui.portal_sessions AS sessions
            JOIN caddy_ui.access_credentials AS credentials
              ON credentials.id = sessions.credential_id
            WHERE sessions.token_hash = @token_hash
              AND sessions.group_id = @group_id
              AND sessions.expires_at > @now
              AND credentials.enabled
            LIMIT 1
            """;
        AddParameter(command, "token_hash", tokenHash);
        AddParameter(command, "group_id", groupId);
        AddParameter(command, "now", DateTimeOffset.UtcNow);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !string.Equals(reader.GetString(2), Limit(userAgent, 400), StringComparison.Ordinal))
        {
            return null;
        }

        return new ValidatedPortalSession(
            reader.GetString(0),
            tokenHash,
            ReadTimestamp(reader, 1));
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

    private static UserAccount ReadUser(DbDataReader reader, int offset = 0)
    {
        return new UserAccount(
            reader.GetGuid(offset),
            reader.GetString(offset + 1),
            reader.GetString(offset + 2),
            reader.GetString(offset + 3),
            reader.GetString(offset + 4),
            reader.GetBoolean(offset + 5),
            reader.IsDBNull(offset + 6) ? null : (byte[])reader.GetValue(offset + 6),
            reader.GetBoolean(offset + 7),
            reader.GetString(offset + 8));
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

    private static string CreateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string HashToken(string token)
    {
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static string Limit(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
    }
}
