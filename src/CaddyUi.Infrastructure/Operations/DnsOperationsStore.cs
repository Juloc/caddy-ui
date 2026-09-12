using System.Data;
using System.Data.Common;
using System.Globalization;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Operations;

public sealed class DnsOperationsStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public DnsOperationsStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<DnsProviderRuntimeRecord?> GetProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, provider_type, label, enabled, config_json::text,
                   secret_references_json::text, last_tested_at,
                   last_test_status, last_test_error
            FROM caddy_ui.dns_providers
            WHERE id = @id
            LIMIT 1
            """;
        AddParameter(command, "id", providerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProvider(reader) : null;
    }

    public Task RecordProviderTestAsync(Guid providerId, ProviderOperationResult result, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            UPDATE caddy_ui.dns_providers
            SET last_tested_at = @now,
                last_test_status = @status,
                last_test_error = @error,
                updated_at = @now
            WHERE id = @id
            """,
            command =>
            {
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "status", result.Succeeded ? "ok" : "failed");
                AddParameter(command, "error", result.Succeeded ? string.Empty : Limit(result.Message, 2000));
                AddParameter(command, "id", providerId);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<ManagedDnsRecord>> ListDnsRecordsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT records.id, records.domain_id, domains.name, records.provider_id,
                   providers.label, records.name, records.record_type, records.value,
                   records.ttl, records.priority, records.enabled, records.source,
                   records.last_sync_at, records.last_sync_status, records.last_sync_error,
                   records.updated_at
            FROM caddy_ui.managed_dns_records AS records
            JOIN caddy_ui.managed_domains AS domains ON domains.id = records.domain_id
            JOIN caddy_ui.dns_providers AS providers ON providers.id = records.provider_id
            ORDER BY domains.name, lower(records.name), records.record_type, records.value
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ManagedDnsRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ManagedDnsRecord(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.GetBoolean(10), reader.GetString(11),
                reader.IsDBNull(12) ? null : ReadTimestamp(reader, 12), reader.GetString(13), reader.GetString(14),
                ReadTimestamp(reader, 15)));
        }

        return result;
    }

    public async Task<Guid> CreateDnsRecordAsync(
        Guid domainId,
        Guid providerId,
        string name,
        string recordType,
        string value,
        int ttl,
        int? priority,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var normalizedName = NormalizeRecordName(name);
        var normalizedType = NormalizeRecordType(recordType);
        var normalizedValue = Required(value, 4000, "Record value");
        var normalizedTtl = Math.Clamp(ttl, 30, 86400);
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.managed_dns_records(
                id, domain_id, provider_id, name, record_type, value, ttl, priority,
                enabled, source, created_at, updated_at, last_sync_status, last_sync_error)
            SELECT @id, domains.id, providers.id, @name, @record_type, @value, @ttl, @priority,
                   true, 'manual', @now, @now, 'pending', ''
            FROM caddy_ui.managed_domains AS domains
            JOIN caddy_ui.dns_providers AS providers ON providers.id = @provider_id
            WHERE domains.id = @domain_id
              AND domains.enabled
              AND providers.enabled
              AND domains.dns_provider_id = providers.id
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "domain_id", domainId);
                AddParameter(command, "provider_id", providerId);
                AddParameter(command, "name", normalizedName);
                AddParameter(command, "record_type", normalizedType);
                AddParameter(command, "value", normalizedValue);
                AddParameter(command, "ttl", normalizedTtl);
                AddParameter(command, "priority", priority);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
            },
            cancellationToken,
            expectedRows: 1,
            failureMessage: "The selected domain/provider assignment is invalid or disabled.");
        return id;
    }

    public Task SetDnsRecordEnabledAsync(Guid recordId, bool enabled, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "UPDATE caddy_ui.managed_dns_records SET enabled = @enabled, updated_at = @now WHERE id = @id",
            command =>
            {
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "id", recordId);
            },
            cancellationToken);
    }

    public Task MarkDnsRecordSyncAsync(Guid recordId, ProviderOperationResult result, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            UPDATE caddy_ui.managed_dns_records
            SET last_sync_at = @now,
                last_sync_status = @status,
                last_sync_error = @error,
                updated_at = @now
            WHERE id = @id
            """,
            command =>
            {
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "status", result.Succeeded ? "ok" : "failed");
                AddParameter(command, "error", result.Succeeded ? string.Empty : Limit(result.Message, 2000));
                AddParameter(command, "id", recordId);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<DdnsTargetRecord>> ListDdnsTargetsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT targets.id, targets.domain_id, domains.name, targets.provider_id,
                   providers.label, targets.name, targets.record_type, targets.enabled,
                   targets.interval_seconds, targets.address_source, targets.static_value,
                   targets.last_value, targets.next_run_at, targets.last_run_at,
                   targets.last_status, targets.last_error, targets.updated_at
            FROM caddy_ui.ddns_targets AS targets
            JOIN caddy_ui.managed_domains AS domains ON domains.id = targets.domain_id
            JOIN caddy_ui.dns_providers AS providers ON providers.id = targets.provider_id
            ORDER BY targets.enabled DESC, domains.name, lower(targets.name), targets.record_type
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DdnsTargetRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDdns(reader));
        }

        return result;
    }

    public async Task<Guid> CreateDdnsTargetAsync(
        Guid domainId,
        Guid providerId,
        string name,
        string recordType,
        int intervalSeconds,
        string addressSource,
        string staticValue,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var type = recordType.Trim().ToUpperInvariant();
        if (type is not ("A" or "AAAA"))
        {
            throw new ArgumentException("DDNS supports A and AAAA records only.", nameof(recordType));
        }

        var source = addressSource.Trim().ToLowerInvariant() == "static" ? "static" : "public";
        if (source == "static" && !System.Net.IPAddress.TryParse(staticValue, out _))
        {
            throw new ArgumentException("A valid static IP address is required.", nameof(staticValue));
        }

        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.ddns_targets(
                id, domain_id, provider_id, name, record_type, enabled,
                interval_seconds, address_source, static_value, next_run_at,
                created_at, updated_at)
            SELECT @id, domains.id, providers.id, @name, @record_type, true,
                   @interval_seconds, @address_source, @static_value, @now, @now, @now
            FROM caddy_ui.managed_domains AS domains
            JOIN caddy_ui.dns_providers AS providers ON providers.id = @provider_id
            WHERE domains.id = @domain_id
              AND domains.dns_provider_id = providers.id
              AND domains.enabled
              AND providers.enabled
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "domain_id", domainId);
                AddParameter(command, "provider_id", providerId);
                AddParameter(command, "name", NormalizeRecordName(name));
                AddParameter(command, "record_type", type);
                AddParameter(command, "interval_seconds", Math.Clamp(intervalSeconds, 60, 86400));
                AddParameter(command, "address_source", source);
                AddParameter(command, "static_value", source == "static" ? staticValue.Trim() : string.Empty);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
            },
            cancellationToken,
            expectedRows: 1,
            failureMessage: "The selected domain/provider assignment is invalid or disabled.");
        return id;
    }

    public Task SetDdnsTargetEnabledAsync(Guid targetId, bool enabled, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "UPDATE caddy_ui.ddns_targets SET enabled = @enabled, next_run_at = @now, updated_at = @now WHERE id = @id",
            command =>
            {
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "id", targetId);
            },
            cancellationToken);
    }

    public async Task<DdnsTargetRecord?> ClaimDueDdnsTargetAsync(string workerId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT targets.id, targets.domain_id, domains.name, targets.provider_id,
                   providers.label, targets.name, targets.record_type, targets.enabled,
                   targets.interval_seconds, targets.address_source, targets.static_value,
                   targets.last_value, targets.next_run_at, targets.last_run_at,
                   targets.last_status, targets.last_error, targets.updated_at
            FROM caddy_ui.ddns_targets AS targets
            JOIN caddy_ui.managed_domains AS domains ON domains.id = targets.domain_id
            JOIN caddy_ui.dns_providers AS providers ON providers.id = targets.provider_id
            WHERE targets.enabled
              AND domains.enabled
              AND providers.enabled
              AND targets.next_run_at <= @now
            ORDER BY targets.next_run_at
            FOR UPDATE OF targets SKIP LOCKED
            LIMIT 1
            """;
        AddParameter(command, "now", DateTimeOffset.UtcNow);
        DdnsTargetRecord? target = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                target = ReadDdns(reader);
            }
        }

        if (target is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE caddy_ui.ddns_targets
                SET next_run_at = @next_run_at,
                    last_status = 'running',
                    last_error = '',
                    updated_at = @now
                WHERE id = @id
                """;
            AddParameter(update, "next_run_at", DateTimeOffset.UtcNow.AddSeconds(target.IntervalSeconds));
            AddParameter(update, "now", DateTimeOffset.UtcNow);
            AddParameter(update, "id", target.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return target;
    }

    public Task CompleteDdnsTargetAsync(Guid targetId, string value, ProviderOperationResult result, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            UPDATE caddy_ui.ddns_targets
            SET last_value = @last_value,
                last_run_at = @now,
                last_status = @status,
                last_error = @error,
                updated_at = @now
            WHERE id = @id
            """,
            command =>
            {
                AddParameter(command, "last_value", value);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "status", result.Succeeded ? "ok" : "failed");
                AddParameter(command, "error", result.Succeeded ? string.Empty : Limit(result.Message, 2000));
                AddParameter(command, "id", targetId);
            },
            cancellationToken);
    }

    private static DnsProviderRuntimeRecord ReadProvider(DbDataReader reader)
    {
        return new DnsProviderRuntimeRecord(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : ReadTimestamp(reader, 6),
            reader.GetString(7), reader.GetString(8));
    }

    private static DdnsTargetRecord ReadDdns(DbDataReader reader)
    {
        return new DdnsTargetRecord(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetBoolean(7), reader.GetInt32(8), reader.GetString(9),
            reader.GetString(10), reader.GetString(11), ReadTimestamp(reader, 12), reader.IsDBNull(13) ? null : ReadTimestamp(reader, 13),
            reader.GetString(14), reader.GetString(15), ReadTimestamp(reader, 16));
    }

    private async Task ExecuteAsync(
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken,
        int? expectedRows = null,
        string failureMessage = "The requested operation did not update the expected row.")
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (expectedRows is int expected && rows != expected)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }

    private static string NormalizeRecordName(string? value)
    {
        var candidate = value?.Trim().TrimEnd('.') ?? string.Empty;
        if (candidate.Length == 0)
        {
            return "@";
        }

        if (candidate.Length > 253 || candidate.Contains(' ') || candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains('\r') || candidate.Contains('\n'))
        {
            throw new ArgumentException("The DNS record name is invalid.", nameof(value));
        }

        return candidate.ToLowerInvariant();
    }

    private static string NormalizeRecordType(string value)
    {
        var type = value.Trim().ToUpperInvariant();
        return type is "A" or "AAAA" or "CNAME" or "TXT" or "MX" or "CAA" or "SRV"
            ? type
            : throw new ArgumentException("Unsupported DNS record type.", nameof(value));
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

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static async Task<DbConnection> OpenConnectionAsync(CaddyUiDbContext context, CancellationToken cancellationToken)
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
            _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, CultureInfo.InvariantCulture),
        };
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
