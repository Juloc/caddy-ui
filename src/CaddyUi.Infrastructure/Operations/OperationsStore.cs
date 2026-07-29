using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Operations;

public sealed class OperationsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public OperationsStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
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

    public async Task RecordProviderTestAsync(Guid providerId, ProviderOperationResult result, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
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

    public async Task MarkDnsRecordSyncAsync(Guid recordId, ProviderOperationResult result, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
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

    public async Task<IReadOnlyList<NotificationChannelRecord>> ListNotificationChannelsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, channel_type, enabled, config_json::text,
                   secret_references_json::text, last_tested_at,
                   last_test_status, last_test_error, updated_at
            FROM caddy_ui.notification_channels
            ORDER BY enabled DESC, lower(name)
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NotificationChannelRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new NotificationChannelRecord(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : ReadTimestamp(reader, 6),
                reader.GetString(7), reader.GetString(8), ReadTimestamp(reader, 9)));
        }

        return result;
    }

    public async Task<Guid> CreateNotificationChannelAsync(string name, string channelType, string configJson, string secretReferencesJson, CancellationToken cancellationToken = default)
    {
        var type = channelType.Trim().ToLowerInvariant();
        if (type is not ("email" or "webhook" or "discord" or "telegram"))
        {
            throw new ArgumentException("Unsupported notification channel type.", nameof(channelType));
        }

        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.notification_channels(
                id, name, channel_type, enabled, config_json, secret_references_json,
                created_at, updated_at)
            VALUES(@id, @name, @channel_type, true, CAST(@config_json AS jsonb),
                   CAST(@secret_references_json AS jsonb), @now, @now)
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "name", Required(name, 120, "Channel name"));
                AddParameter(command, "channel_type", type);
                AddParameter(command, "config_json", NormalizeObjectJson(configJson));
                AddParameter(command, "secret_references_json", NormalizeObjectJson(secretReferencesJson));
                AddParameter(command, "now", DateTimeOffset.UtcNow);
            },
            cancellationToken);
        return id;
    }

    public Task SetNotificationChannelEnabledAsync(Guid channelId, bool enabled, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "UPDATE caddy_ui.notification_channels SET enabled = @enabled, updated_at = @now WHERE id = @id",
            command =>
            {
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "id", channelId);
            },
            cancellationToken);
    }

    public Task RecordNotificationChannelTestAsync(Guid channelId, ProviderOperationResult result, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            UPDATE caddy_ui.notification_channels
            SET last_tested_at = @now, last_test_status = @status,
                last_test_error = @error, updated_at = @now
            WHERE id = @id
            """,
            command =>
            {
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "status", result.Succeeded ? "ok" : "failed");
                AddParameter(command, "error", result.Succeeded ? string.Empty : Limit(result.Message, 2000));
                AddParameter(command, "id", channelId);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduledJobRecord>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, job_type, enabled, interval_seconds, config_json::text,
                   next_run_at, last_run_at, last_status, last_error, updated_at
            FROM caddy_ui.scheduled_jobs
            ORDER BY enabled DESC, next_run_at, lower(name)
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ScheduledJobRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadJob(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<JobRunRecord>> ListJobRunsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT runs.id, runs.job_id, jobs.name, runs.started_at, runs.completed_at,
                   runs.status, runs.message, runs.details_json::text, runs.correlation_id
            FROM caddy_ui.job_runs AS runs
            JOIN caddy_ui.scheduled_jobs AS jobs ON jobs.id = runs.job_id
            ORDER BY runs.started_at DESC
            LIMIT @limit
            """;
        AddParameter(command, "limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<JobRunRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new JobRunRecord(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), ReadTimestamp(reader, 3),
                reader.IsDBNull(4) ? null : ReadTimestamp(reader, 4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8)));
        }

        return result;
    }

    public async Task<Guid> CreateJobAsync(string name, string jobType, int intervalSeconds, string configJson, CancellationToken cancellationToken = default)
    {
        var type = jobType.Trim().ToLowerInvariant();
        if (type is not ("ddns" or "provider-test" or "health" or "backup"))
        {
            throw new ArgumentException("Unsupported scheduled job type.", nameof(jobType));
        }

        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.scheduled_jobs(
                id, name, job_type, enabled, interval_seconds, config_json,
                next_run_at, created_at, updated_at)
            VALUES(@id, @name, @job_type, true, @interval_seconds,
                   CAST(@config_json AS jsonb), @now, @now, @now)
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "name", Required(name, 120, "Job name"));
                AddParameter(command, "job_type", type);
                AddParameter(command, "interval_seconds", Math.Clamp(intervalSeconds, 60, 604800));
                AddParameter(command, "config_json", NormalizeObjectJson(configJson));
                AddParameter(command, "now", DateTimeOffset.UtcNow);
            },
            cancellationToken);
        return id;
    }

    public Task SetJobEnabledAsync(Guid jobId, bool enabled, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "UPDATE caddy_ui.scheduled_jobs SET enabled = @enabled, next_run_at = @now, updated_at = @now WHERE id = @id",
            command =>
            {
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "id", jobId);
            },
            cancellationToken);
    }

    public async Task<ScheduledJobRecord?> ClaimDueJobAsync(string workerId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, name, job_type, enabled, interval_seconds, config_json::text,
                   next_run_at, last_run_at, last_status, last_error, updated_at
            FROM caddy_ui.scheduled_jobs
            WHERE enabled
              AND next_run_at <= @now
              AND (locked_at IS NULL OR locked_at < @stale)
            ORDER BY next_run_at
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """;
        AddParameter(command, "now", DateTimeOffset.UtcNow);
        AddParameter(command, "stale", DateTimeOffset.UtcNow.AddMinutes(-15));
        ScheduledJobRecord? job = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                job = ReadJob(reader);
            }
        }

        if (job is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE caddy_ui.scheduled_jobs
                SET locked_at = @now, lock_owner = @worker_id,
                    next_run_at = @next_run_at, last_status = 'running',
                    last_error = '', updated_at = @now
                WHERE id = @id
                """;
            AddParameter(update, "now", DateTimeOffset.UtcNow);
            AddParameter(update, "worker_id", workerId);
            AddParameter(update, "next_run_at", DateTimeOffset.UtcNow.AddSeconds(job.IntervalSeconds));
            AddParameter(update, "id", job.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public async Task<Guid> StartJobRunAsync(Guid jobId, string correlationId, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.job_runs(
                id, job_id, started_at, status, correlation_id)
            VALUES(@id, @job_id, @now, 'running', @correlation_id)
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "job_id", jobId);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "correlation_id", correlationId);
            },
            cancellationToken);
        return id;
    }

    public async Task CompleteJobRunAsync(Guid jobId, Guid runId, ProviderOperationResult result, string detailsJson, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var run = connection.CreateCommand())
            {
                run.Transaction = transaction;
                run.CommandText =
                    """
                    UPDATE caddy_ui.job_runs
                    SET completed_at = @now, status = @status,
                        message = @message, details_json = CAST(@details_json AS jsonb)
                    WHERE id = @run_id
                    """;
                AddParameter(run, "now", DateTimeOffset.UtcNow);
                AddParameter(run, "status", result.Succeeded ? "ok" : "failed");
                AddParameter(run, "message", Limit(result.Message, 4000));
                AddParameter(run, "details_json", NormalizeObjectJson(detailsJson));
                AddParameter(run, "run_id", runId);
                await run.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var job = connection.CreateCommand())
            {
                job.Transaction = transaction;
                job.CommandText =
                    """
                    UPDATE caddy_ui.scheduled_jobs
                    SET last_run_at = @now, last_status = @status,
                        last_error = @error, locked_at = NULL, lock_owner = '', updated_at = @now
                    WHERE id = @job_id
                    """;
                AddParameter(job, "now", DateTimeOffset.UtcNow);
                AddParameter(job, "status", result.Succeeded ? "ok" : "failed");
                AddParameter(job, "error", result.Succeeded ? string.Empty : Limit(result.Message, 2000));
                AddParameter(job, "job_id", jobId);
                await job.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<HealthTargetRecord>> ListHealthTargetsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, target_type, url, enabled, expected_status_min,
                   expected_status_max, timeout_seconds, last_checked_at,
                   last_status, last_http_status, last_duration_ms, last_error, updated_at
            FROM caddy_ui.health_targets
            ORDER BY enabled DESC, target_type, lower(name)
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<HealthTargetRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadHealth(reader));
        }

        return result;
    }

    public async Task<Guid> CreateHealthTargetAsync(string name, string targetType, string url, int statusMin, int statusMax, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var type = targetType.Trim().ToLowerInvariant();
        if (type is not ("public" or "upstream"))
        {
            throw new ArgumentException("Health target type must be public or upstream.", nameof(targetType));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A valid HTTP(S) health URL is required.", nameof(url));
        }

        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.health_targets(
                id, name, target_type, url, enabled, expected_status_min,
                expected_status_max, timeout_seconds, created_at, updated_at)
            VALUES(@id, @name, @target_type, @url, true, @status_min,
                   @status_max, @timeout, @now, @now)
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "name", Required(name, 120, "Health target name"));
                AddParameter(command, "target_type", type);
                AddParameter(command, "url", uri.ToString());
                AddParameter(command, "status_min", Math.Clamp(statusMin, 100, 599));
                AddParameter(command, "status_max", Math.Clamp(statusMax, Math.Clamp(statusMin, 100, 599), 599));
                AddParameter(command, "timeout", Math.Clamp(timeoutSeconds, 1, 120));
                AddParameter(command, "now", DateTimeOffset.UtcNow);
            },
            cancellationToken);
        return id;
    }

    public Task SetHealthTargetEnabledAsync(Guid targetId, bool enabled, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            "UPDATE caddy_ui.health_targets SET enabled = @enabled, updated_at = @now WHERE id = @id",
            command =>
            {
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "id", targetId);
            },
            cancellationToken);
    }

    public async Task RecordHealthCheckAsync(Guid targetId, ProviderOperationResult result, int? httpStatus, double? durationMilliseconds, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var history = connection.CreateCommand())
            {
                history.Transaction = transaction;
                history.CommandText =
                    """
                    INSERT INTO caddy_ui.health_checks(
                        target_id, checked_at, status, http_status, duration_ms, error)
                    VALUES(@target_id, @now, @status, @http_status, @duration_ms, @error)
                    """;
                BindHealthResult(history, targetId, result, httpStatus, durationMilliseconds);
                await history.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var target = connection.CreateCommand())
            {
                target.Transaction = transaction;
                target.CommandText =
                    """
                    UPDATE caddy_ui.health_targets
                    SET last_checked_at = @now, last_status = @status,
                        last_http_status = @http_status, last_duration_ms = @duration_ms,
                        last_error = @error, updated_at = @now
                    WHERE id = @target_id
                    """;
                BindHealthResult(target, targetId, result, httpStatus, durationMilliseconds);
                await target.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task InsertNotificationAsync(SystemNotification notification, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.notifications(
                created_at, severity, event_type, title, message, object_type, object_id)
            VALUES(@now, @severity, @event_type, @title, @message, @object_type, @object_id)
            """,
            command =>
            {
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "severity", Limit(notification.Severity, 16));
                AddParameter(command, "event_type", Limit(notification.EventType, 120));
                AddParameter(command, "title", Limit(notification.Title, 300));
                AddParameter(command, "message", Limit(notification.Message, 4000));
                AddParameter(command, "object_type", Limit(notification.ObjectType, 120));
                AddParameter(command, "object_id", Limit(notification.ObjectId, 300));
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<BackupArtifactRecord>> ListBackupsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created_at, file_name, path, size_bytes, digest,
                   status, error, manifest_json::text
            FROM caddy_ui.backup_artifacts
            ORDER BY created_at DESC
            LIMIT 200
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BackupArtifactRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new BackupArtifactRecord(
                reader.GetGuid(0), ReadTimestamp(reader, 1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
        }

        return result;
    }

    public Task RecordBackupAsync(BackupArtifactRecord artifact, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            INSERT INTO caddy_ui.backup_artifacts(
                id, created_at, file_name, path, size_bytes, digest, status, error, manifest_json)
            VALUES(@id, @created_at, @file_name, @path, @size_bytes, @digest,
                   @status, @error, CAST(@manifest_json AS jsonb))
            """,
            command =>
            {
                AddParameter(command, "id", artifact.Id);
                AddParameter(command, "created_at", artifact.CreatedAt);
                AddParameter(command, "file_name", artifact.FileName);
                AddParameter(command, "path", artifact.Path);
                AddParameter(command, "size_bytes", artifact.SizeBytes);
                AddParameter(command, "digest", artifact.Digest);
                AddParameter(command, "status", artifact.Status);
                AddParameter(command, "error", artifact.Error);
                AddParameter(command, "manifest_json", NormalizeObjectJson(artifact.ManifestJson));
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

    private static ScheduledJobRecord ReadJob(DbDataReader reader)
    {
        return new ScheduledJobRecord(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt32(4),
            reader.GetString(5), ReadTimestamp(reader, 6), reader.IsDBNull(7) ? null : ReadTimestamp(reader, 7),
            reader.GetString(8), reader.GetString(9), ReadTimestamp(reader, 10));
    }

    private static HealthTargetRecord ReadHealth(DbDataReader reader)
    {
        return new HealthTargetRecord(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : ReadTimestamp(reader, 8),
            reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetDouble(11),
            reader.GetString(12), ReadTimestamp(reader, 13));
    }

    private static void BindHealthResult(DbCommand command, Guid targetId, ProviderOperationResult result, int? httpStatus, double? durationMilliseconds)
    {
        AddParameter(command, "target_id", targetId);
        AddParameter(command, "now", DateTimeOffset.UtcNow);
        AddParameter(command, "status", result.Succeeded ? "healthy" : "unhealthy");
        AddParameter(command, "http_status", httpStatus);
        AddParameter(command, "duration_ms", durationMilliseconds);
        AddParameter(command, "error", result.Succeeded ? string.Empty : Limit(result.Message, 2000));
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

    private static string NormalizeObjectJson(string? value)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The value must be a JSON object.", nameof(value));
        }

        return document.RootElement.GetRawText();
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
