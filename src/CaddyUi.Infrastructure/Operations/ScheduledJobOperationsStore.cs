using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Operations;

public sealed class ScheduledJobOperationsStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public ScheduledJobOperationsStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<ScheduledJobRecord>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await RelationalStoreSupport.OpenConnectionAsync(context, cancellationToken);
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

    public async Task<IReadOnlyList<JobRunRecord>> ListJobRunsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await RelationalStoreSupport.OpenConnectionAsync(context, cancellationToken);
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
        RelationalStoreSupport.AddParameter(command, "limit", Math.Clamp(limit, 1, 500));
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

    public async Task<Guid> CreateJobAsync(
        string name,
        string jobType,
        int intervalSeconds,
        string configJson,
        CancellationToken cancellationToken = default)
    {
        var type = jobType.Trim().ToLowerInvariant();
        if (type is not ("ddns" or "provider-test" or "health" or "backup"))
        {
            throw new ArgumentException("Unsupported scheduled job type.", nameof(jobType));
        }

        var id = Guid.NewGuid();
        await RelationalStoreSupport.ExecuteNonQueryAsync(
            _contextFactory,
            """
            INSERT INTO caddy_ui.scheduled_jobs(
                id, name, job_type, enabled, interval_seconds, config_json,
                next_run_at, created_at, updated_at)
            VALUES(@id, @name, @job_type, true, @interval_seconds,
                   CAST(@config_json AS jsonb), @now, @now, @now)
            """,
            command =>
            {
                RelationalStoreSupport.AddParameter(command, "id", id);
                RelationalStoreSupport.AddParameter(command, "name", Required(name, 120, "Job name"));
                RelationalStoreSupport.AddParameter(command, "job_type", type);
                RelationalStoreSupport.AddParameter(command, "interval_seconds", Math.Clamp(intervalSeconds, 60, 604800));
                RelationalStoreSupport.AddParameter(command, "config_json", NormalizeObjectJson(configJson));
                RelationalStoreSupport.AddParameter(command, "now", DateTimeOffset.UtcNow);
            },
            cancellationToken);
        return id;
    }

    public Task SetJobEnabledAsync(
        Guid jobId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return RelationalStoreSupport.ExecuteNonQueryAsync(
            _contextFactory,
            "UPDATE caddy_ui.scheduled_jobs SET enabled = @enabled, next_run_at = @now, updated_at = @now WHERE id = @id",
            command =>
            {
                RelationalStoreSupport.AddParameter(command, "enabled", enabled);
                RelationalStoreSupport.AddParameter(command, "now", DateTimeOffset.UtcNow);
                RelationalStoreSupport.AddParameter(command, "id", jobId);
            },
            cancellationToken);
    }

    public async Task<ScheduledJobRecord?> ClaimDueJobAsync(
        string workerId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await RelationalStoreSupport.OpenConnectionAsync(context, cancellationToken);
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
        RelationalStoreSupport.AddParameter(command, "now", DateTimeOffset.UtcNow);
        RelationalStoreSupport.AddParameter(command, "stale", DateTimeOffset.UtcNow.AddMinutes(-15));
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
            RelationalStoreSupport.AddParameter(update, "now", DateTimeOffset.UtcNow);
            RelationalStoreSupport.AddParameter(update, "worker_id", workerId);
            RelationalStoreSupport.AddParameter(update, "next_run_at", DateTimeOffset.UtcNow.AddSeconds(job.IntervalSeconds));
            RelationalStoreSupport.AddParameter(update, "id", job.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public async Task<Guid> StartJobRunAsync(
        Guid jobId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await RelationalStoreSupport.ExecuteNonQueryAsync(
            _contextFactory,
            """
            INSERT INTO caddy_ui.job_runs(
                id, job_id, started_at, status, correlation_id)
            VALUES(@id, @job_id, @now, 'running', @correlation_id)
            """,
            command =>
            {
                RelationalStoreSupport.AddParameter(command, "id", id);
                RelationalStoreSupport.AddParameter(command, "job_id", jobId);
                RelationalStoreSupport.AddParameter(command, "now", DateTimeOffset.UtcNow);
                RelationalStoreSupport.AddParameter(command, "correlation_id", correlationId);
            },
            cancellationToken);
        return id;
    }

    public async Task CompleteJobRunAsync(
        Guid jobId,
        Guid runId,
        ProviderOperationResult result,
        string detailsJson,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await RelationalStoreSupport.OpenConnectionAsync(context, cancellationToken);
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
                RelationalStoreSupport.AddParameter(run, "now", DateTimeOffset.UtcNow);
                RelationalStoreSupport.AddParameter(run, "status", result.Succeeded ? "ok" : "failed");
                RelationalStoreSupport.AddParameter(run, "message", Limit(result.Message, 4000));
                RelationalStoreSupport.AddParameter(run, "details_json", NormalizeObjectJson(detailsJson));
                RelationalStoreSupport.AddParameter(run, "run_id", runId);
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
                RelationalStoreSupport.AddParameter(job, "now", DateTimeOffset.UtcNow);
                RelationalStoreSupport.AddParameter(job, "status", result.Succeeded ? "ok" : "failed");
                RelationalStoreSupport.AddParameter(job, "error", result.Succeeded ? string.Empty : Limit(result.Message, 2000));
                RelationalStoreSupport.AddParameter(job, "job_id", jobId);
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

    private static ScheduledJobRecord ReadJob(DbDataReader reader)
    {
        return new ScheduledJobRecord(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt32(4),
            reader.GetString(5), ReadTimestamp(reader, 6), reader.IsDBNull(7) ? null : ReadTimestamp(reader, 7),
            reader.GetString(8), reader.GetString(9), ReadTimestamp(reader, 10));
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
}
