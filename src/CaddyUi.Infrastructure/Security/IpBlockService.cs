using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CaddyUi.Application.Security;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Security;

public sealed record IpBlockRuleRecord(
    Guid Id,
    string Target,
    string Reason,
    string Source,
    string ActivationState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ReleasedAt,
    Guid? CreatedByUserId,
    string CorrelationId);

public sealed class IpBlockService
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly AtomicBlocklistWriter _writer;
    private readonly IpSecurityOptions _options;
    private readonly TimeProvider _timeProvider;

    public IpBlockService(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        AtomicBlocklistWriter writer,
        IpSecurityOptions options,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory;
        _writer = writer;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<IpBlockRuleRecord> BlockAsync(
        string target,
        string reason,
        DateTimeOffset expiresAt,
        Guid? actorUserId,
        string actorUsername,
        IPAddress? actorAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUsername);
        var network = IpNetworkParser.Parse(target);
        if (!network.IsSingleAddress)
        {
            throw new ArgumentException(
                "The current Caddy guard blocklist supports exact IP addresses only.",
                nameof(target));
        }

        var now = _timeProvider.GetUtcNow();
        if (expiresAt <= now)
        {
            throw new ArgumentException("The block expiration must be in the future.", nameof(expiresAt));
        }

        if (expiresAt > now.AddHours(_options.MaximumBlockHours))
        {
            throw new ArgumentException(
                $"The block duration exceeds the configured maximum of {_options.MaximumBlockHours} hours.",
                nameof(expiresAt));
        }

        var normalizedReason = NormalizeReason(reason);
        var correlationId = Guid.NewGuid().ToString("N");
        var activationState = _options.BlockWriteMode == IpBlockWriteMode.Active
            ? "active"
            : "shadow";
        const string source = "manual";

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        BlocklistWriteReceipt? receipt = null;
        try
        {
            var existing = await FindActiveRuleAsync(
                connection,
                transaction,
                network.Cidr,
                cancellationToken);
            var id = existing?.Id ?? Guid.NewGuid();
            var before = existing is null ? "{}" : JsonSerializer.Serialize(existing);
            if (existing is null)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO caddy_ui.ip_block_rules(
                        id, address_or_network, reason, source, enabled,
                        created_at, expires_at, released_at, created_by_user_id,
                        activation_state, correlation_id, updated_at)
                    VALUES(
                        @id, CAST(@target AS cidr), @reason, @source, true,
                        @now, @expires_at, NULL, @actor_user_id,
                        @activation_state, @correlation_id, @now)
                    """,
                    command => BindRule(
                        command,
                        id,
                        network.Cidr,
                        normalizedReason,
                        source,
                        now,
                        expiresAt,
                        actorUserId,
                        activationState,
                        correlationId),
                    cancellationToken);
            }
            else
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE caddy_ui.ip_block_rules
                    SET reason = @reason,
                        source = @source,
                        enabled = true,
                        expires_at = @expires_at,
                        released_at = NULL,
                        created_by_user_id = @actor_user_id,
                        activation_state = @activation_state,
                        correlation_id = @correlation_id,
                        updated_at = @now
                    WHERE id = @id
                    """,
                    command => BindRule(
                        command,
                        id,
                        network.Cidr,
                        normalizedReason,
                        source,
                        now,
                        expiresAt,
                        actorUserId,
                        activationState,
                        correlationId),
                    cancellationToken);
            }

            if (_options.BlockWriteMode != IpBlockWriteMode.Disabled)
            {
                var entries = await ListFileEntriesAsync(
                    connection,
                    transaction,
                    activationState,
                    now,
                    cancellationToken);
                receipt = await _writer.ApplyAsync(
                    _options.BlocklistPath,
                    entries,
                    cancellationToken);
            }

            var result = new IpBlockRuleRecord(
                id,
                network.Cidr,
                normalizedReason,
                source,
                activationState,
                existing?.CreatedAt ?? now,
                now,
                expiresAt,
                null,
                actorUserId,
                correlationId);
            await WriteHistoryAsync(
                connection,
                transaction,
                result,
                existing is null ? "created" : "updated",
                actorUserId,
                normalizedReason,
                correlationId,
                cancellationToken);
            await WriteSecurityAndAuditAsync(
                connection,
                transaction,
                result,
                actorUserId,
                actorUsername,
                actorAddress,
                before,
                JsonSerializer.Serialize(result),
                "block",
                correlationId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            if (receipt is not null)
            {
                await _writer.RollbackAsync(receipt, cancellationToken);
            }

            throw;
        }
    }

    public async Task<IpBlockRuleRecord> UnblockAsync(
        Guid ruleId,
        string reason,
        Guid? actorUserId,
        string actorUsername,
        IPAddress? actorAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUsername);
        var normalizedReason = NormalizeReason(reason);
        var now = _timeProvider.GetUtcNow();
        var correlationId = Guid.NewGuid().ToString("N");

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        BlocklistWriteReceipt? receipt = null;
        try
        {
            var existing = await FindRuleAsync(connection, transaction, ruleId, cancellationToken) ??
                throw new InvalidOperationException("The selected block rule does not exist.");
            if (existing.ReleasedAt is not null)
            {
                return existing;
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE caddy_ui.ip_block_rules
                SET enabled = false,
                    released_at = @released_at,
                    activation_state = 'released',
                    correlation_id = @correlation_id,
                    updated_at = @released_at
                WHERE id = @id
                """,
                command =>
                {
                    AddParameter(command, "released_at", now);
                    AddParameter(command, "correlation_id", correlationId);
                    AddParameter(command, "id", ruleId);
                },
                cancellationToken);

            if (_options.BlockWriteMode != IpBlockWriteMode.Disabled)
            {
                var activeState = _options.BlockWriteMode == IpBlockWriteMode.Active
                    ? "active"
                    : "shadow";
                var entries = await ListFileEntriesAsync(
                    connection,
                    transaction,
                    activeState,
                    now,
                    cancellationToken);
                receipt = await _writer.ApplyAsync(
                    _options.BlocklistPath,
                    entries,
                    cancellationToken);
            }

            var result = existing with
            {
                ActivationState = "released",
                UpdatedAt = now,
                ReleasedAt = now,
                CorrelationId = correlationId,
            };
            await WriteHistoryAsync(
                connection,
                transaction,
                result,
                "released",
                actorUserId,
                normalizedReason,
                correlationId,
                cancellationToken);
            await WriteSecurityAndAuditAsync(
                connection,
                transaction,
                result,
                actorUserId,
                actorUsername,
                actorAddress,
                JsonSerializer.Serialize(existing),
                JsonSerializer.Serialize(result),
                "unblock",
                correlationId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            if (receipt is not null)
            {
                await _writer.RollbackAsync(receipt, cancellationToken);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<IpBlockRuleRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, address_or_network::text, reason, source, activation_state,
                   created_at, updated_at, expires_at, released_at,
                   created_by_user_id, correlation_id
            FROM caddy_ui.ip_block_rules
            ORDER BY created_at DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<IpBlockRuleRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRule(reader));
        }

        return result;
    }

    private static void BindRule(
        DbCommand command,
        Guid id,
        string target,
        string reason,
        string source,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        Guid? actorUserId,
        string activationState,
        string correlationId)
    {
        AddParameter(command, "id", id);
        AddParameter(command, "target", target);
        AddParameter(command, "reason", reason);
        AddParameter(command, "source", source);
        AddParameter(command, "now", now);
        AddParameter(command, "expires_at", expiresAt);
        AddParameter(command, "actor_user_id", actorUserId);
        AddParameter(command, "activation_state", activationState);
        AddParameter(command, "correlation_id", correlationId);
    }

    private static async Task<IpBlockRuleRecord?> FindActiveRuleAsync(
        DbConnection connection,
        DbTransaction transaction,
        string target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, address_or_network::text, reason, source, activation_state,
                   created_at, updated_at, expires_at, released_at,
                   created_by_user_id, correlation_id
            FROM caddy_ui.ip_block_rules
            WHERE address_or_network = CAST(@target AS cidr)
              AND enabled
              AND released_at IS NULL
            LIMIT 1
            FOR UPDATE
            """;
        AddParameter(command, "target", target);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRule(reader) : null;
    }

    private static async Task<IpBlockRuleRecord?> FindRuleAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, address_or_network::text, reason, source, activation_state,
                   created_at, updated_at, expires_at, released_at,
                   created_by_user_id, correlation_id
            FROM caddy_ui.ip_block_rules
            WHERE id = @id
            FOR UPDATE
            """;
        AddParameter(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRule(reader) : null;
    }

    private static async Task<IReadOnlyList<BlocklistEntry>> ListFileEntriesAsync(
        DbConnection connection,
        DbTransaction transaction,
        string activationState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT host(address_or_network), expires_at, reason
            FROM caddy_ui.ip_block_rules
            WHERE enabled
              AND released_at IS NULL
              AND expires_at > @now
              AND activation_state = @activation_state
            ORDER BY address_or_network
            """;
        AddParameter(command, "now", now);
        AddParameter(command, "activation_state", activationState);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BlocklistEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (IPAddress.TryParse(reader.GetString(0), out var address))
            {
                result.Add(
                    new BlocklistEntry(
                        IpAddressClassifier.Normalize(address),
                        ReadTimestamp(reader, 1),
                        reader.GetString(2)));
            }
        }

        return result;
    }

    private static Task WriteHistoryAsync(
        DbConnection connection,
        DbTransaction transaction,
        IpBlockRuleRecord rule,
        string action,
        Guid? actorUserId,
        string reason,
        string correlationId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.ip_block_history(
                rule_id, occurred_at, action, actor_user_id, reason,
                snapshot_json, correlation_id, details_json)
            VALUES(
                @rule_id, @occurred_at, @action, @actor_user_id, @reason,
                CAST(@snapshot_json AS jsonb), @correlation_id, '{}'::jsonb)
            """,
            command =>
            {
                AddParameter(command, "rule_id", rule.Id);
                AddParameter(command, "occurred_at", rule.UpdatedAt);
                AddParameter(command, "action", action);
                AddParameter(command, "actor_user_id", actorUserId);
                AddParameter(command, "reason", reason);
                AddParameter(command, "snapshot_json", JsonSerializer.Serialize(rule));
                AddParameter(command, "correlation_id", correlationId);
            },
            cancellationToken);
    }

    private static async Task WriteSecurityAndAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        IpBlockRuleRecord rule,
        Guid? actorUserId,
        string actorUsername,
        IPAddress? actorAddress,
        string beforeJson,
        string afterJson,
        string action,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.security_events(
                occurred_at, kind, reason, remote_address,
                anonymous_client_id, host, path, details_json)
            VALUES(
                @occurred_at, @kind, @reason,
                CAST(split_part(@target, '/', 1) AS inet),
                NULL, '', '', CAST(@details_json AS jsonb))
            """,
            command =>
            {
                AddParameter(command, "occurred_at", rule.UpdatedAt);
                AddParameter(command, "kind", $"ip-{action}");
                AddParameter(command, "reason", rule.Reason);
                AddParameter(command, "target", rule.Target);
                AddParameter(
                    command,
                    "details_json",
                    JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["ruleId"] = rule.Id,
                        ["activationState"] = rule.ActivationState,
                        ["correlationId"] = correlationId,
                    }));
            },
            cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.audit_events(
                occurred_at, actor_user_id, actor_username, remote_address,
                action, object_type, object_id, before_json, after_json,
                result, revision_id, correlation_id)
            VALUES(
                @occurred_at, @actor_user_id, @actor_username, @remote_address,
                @action, 'ip-block-rule', @object_id,
                CAST(@before_json AS jsonb), CAST(@after_json AS jsonb),
                'succeeded', NULL, @correlation_id)
            """,
            command =>
            {
                AddParameter(command, "occurred_at", rule.UpdatedAt);
                AddParameter(command, "actor_user_id", actorUserId);
                AddParameter(command, "actor_username", actorUsername);
                AddParameter(command, "remote_address", actorAddress);
                AddParameter(command, "action", $"ip-block.{action}");
                AddParameter(command, "object_id", rule.Id.ToString("D"));
                AddParameter(command, "before_json", beforeJson);
                AddParameter(command, "after_json", afterJson);
                AddParameter(command, "correlation_id", correlationId);
            },
            cancellationToken);
    }

    private static IpBlockRuleRecord ReadRule(DbDataReader reader)
    {
        return new IpBlockRuleRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ReadTimestamp(reader, 5),
            ReadTimestamp(reader, 6),
            reader.IsDBNull(7) ? null : ReadTimestamp(reader, 7),
            reader.IsDBNull(8) ? null : ReadTimestamp(reader, 8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.GetString(10));
    }

    private static string NormalizeReason(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A block reason is required.", nameof(value));
        }

        return normalized.Length <= 500 ? normalized : normalized[..500];
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

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        bind(command);
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
