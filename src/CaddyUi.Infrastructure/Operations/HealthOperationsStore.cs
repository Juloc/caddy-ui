using System.Data.Common;
using System.Globalization;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static CaddyUi.Infrastructure.Persistence.RelationalStoreSupport;

namespace CaddyUi.Infrastructure.Operations;

public sealed class HealthOperationsStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public HealthOperationsStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
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

    public async Task<Guid> CreateHealthTargetAsync(
        string name,
        string targetType,
        string url,
        int statusMin,
        int statusMax,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
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
        await ExecuteNonQueryAsync(
            _contextFactory,
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
        return ExecuteNonQueryAsync(
            _contextFactory,
            "UPDATE caddy_ui.health_targets SET enabled = @enabled, updated_at = @now WHERE id = @id",
            command =>
            {
                AddParameter(command, "enabled", enabled);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "id", targetId);
            },
            cancellationToken);
    }

    public async Task RecordHealthCheckAsync(
        Guid targetId,
        ProviderOperationResult result,
        int? httpStatus,
        double? durationMilliseconds,
        CancellationToken cancellationToken = default)
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

    private static HealthTargetRecord ReadHealth(DbDataReader reader)
    {
        return new HealthTargetRecord(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : ReadTimestamp(reader, 8),
            reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetDouble(11),
            reader.GetString(12), ReadTimestamp(reader, 13));
    }

    private static void BindHealthResult(
        DbCommand command,
        Guid targetId,
        ProviderOperationResult result,
        int? httpStatus,
        double? durationMilliseconds)
    {
        AddParameter(command, "target_id", targetId);
        AddParameter(command, "now", DateTimeOffset.UtcNow);
        AddParameter(command, "status", result.Succeeded ? "healthy" : "unhealthy");
        AddParameter(command, "http_status", httpStatus);
        AddParameter(command, "duration_ms", durationMilliseconds);
        AddParameter(command, "error", result.Succeeded ? string.Empty : Limit(result.Message, 2000));
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
            _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, CultureInfo.InvariantCulture),
        };
    }
}
