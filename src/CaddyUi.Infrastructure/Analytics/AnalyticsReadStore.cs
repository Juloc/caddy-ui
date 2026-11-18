using System.Data;
using System.Data.Common;
using System.Globalization;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Analytics;

public sealed record AnalyticsReadFilter(
    DateTimeOffset From,
    DateTimeOffset To,
    string Host,
    string ActorType,
    string RequestType,
    string StatusClass,
    int Limit)
{
    private static readonly string[] ActorTypes = ["human", "bot", "internal", "unknown"];
    private static readonly string[] RequestTypes =
    [
        "document",
        "asset",
        "api",
        "websocket",
        "healthcheck",
        "auth",
        "system",
        "other",
    ];
    private static readonly string[] StatusClasses = ["1xx", "2xx", "3xx", "4xx", "5xx"];

    public static AnalyticsReadFilter Create(
        DateTimeOffset from,
        DateTimeOffset to,
        string? host = null,
        string? actorType = null,
        string? requestType = null,
        string? statusClass = null,
        int limit = 200)
    {
        var normalizedTo = to.ToUniversalTime();
        var normalizedFrom = from.ToUniversalTime();
        if (normalizedFrom >= normalizedTo)
        {
            normalizedFrom = normalizedTo.AddHours(-24);
        }

        var earliest = normalizedTo.AddDays(-366);
        if (normalizedFrom < earliest)
        {
            normalizedFrom = earliest;
        }

        return new AnalyticsReadFilter(
            normalizedFrom,
            normalizedTo,
            NormalizeHost(host),
            NormalizeDimension(actorType, ActorTypes),
            NormalizeDimension(requestType, RequestTypes),
            NormalizeDimension(statusClass, StatusClasses),
            Math.Clamp(limit, 1, 1000));
    }

    private static string NormalizeHost(string? value)
    {
        var host = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return host.Length <= 253 ? host : host[..253];
    }

    private static string NormalizeDimension(string? value, IReadOnlyCollection<string> allowed)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized, StringComparer.Ordinal) ? normalized : string.Empty;
    }
}

public sealed record RequestAnalyticsRow(
    DateTimeOffset OccurredAt,
    string Host,
    string Method,
    string Path,
    int Status,
    string RequestType,
    string ActorType,
    double DurationMilliseconds,
    long BytesSent,
    string RemoteAddress,
    string ClientId);

public sealed record SuspiciousClientRow(
    Guid ClientId,
    string ClientKey,
    bool Estimated,
    DateTimeOffset LastSeenAt,
    string RemoteAddress,
    string Classification,
    int AutomationScore,
    string RiskLevel,
    DateTimeOffset? AssessedAt);

public sealed record SecurityEventAnalyticsRow(
    DateTimeOffset OccurredAt,
    string Kind,
    string Reason,
    string RemoteAddress,
    string Host,
    string Path);

public sealed record SecurityAnalyticsSnapshot(
    long Clients,
    long BotRequests,
    long HighRiskClients,
    long MediumRiskClients,
    long ActiveBlocks,
    IReadOnlyList<SuspiciousClientRow> SuspiciousClients,
    IReadOnlyList<SecurityEventAnalyticsRow> RecentEvents);

public sealed record TrafficSeriesPoint(DateTimeOffset BucketStart, long Requests, long BotRequests, long BytesSent, long Errors);
public sealed record DomainTrafficRow(string Host, long Requests, long BotRequests, long Errors, long BytesSent);
public sealed record AnalyticsDashboardSnapshot(long Requests, long BotRequests, long Errors, long BytesSent,
    double ErrorRatePercent, double BotSharePercent, IReadOnlyList<TrafficSeriesPoint> Series, IReadOnlyList<DomainTrafficRow> Domains);
public sealed record SystemAnalyticsSnapshot(bool DatabaseAvailable, string DatabaseError, bool IngestionEnabled, int ConfiguredLogPaths,
    DateTimeOffset? LatestRequestAt, DateTimeOffset? LatestCheckpointAt, long CheckpointCount, long UnresolvedFailures,
    long TrafficBucketRows, long ClientRows, int StatisticsRetentionDays, int SecurityEventRetentionDays,
    bool IpIntelligenceEnabled, bool RiskAssessmentEnabled, string BlockWriteMode);

public sealed class AnalyticsReadStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly AnalyticsIngestionOptions _analyticsOptions;
    private readonly IpSecurityOptions _ipSecurityOptions;

    public AnalyticsReadStore(IDbContextFactory<CaddyUiDbContext> contextFactory, AnalyticsIngestionOptions analyticsOptions, IpSecurityOptions ipSecurityOptions)
    {
        _contextFactory = contextFactory;
        _analyticsOptions = analyticsOptions;
        _ipSecurityOptions = ipSecurityOptions;
    }

    public async Task<IReadOnlyList<string>> ListHostsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT host FROM (SELECT host FROM caddy_ui.managed_routes UNION SELECT name FROM caddy_ui.managed_domains) AS hosts WHERE host <> '' ORDER BY host LIMIT 500";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var hosts = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            hosts.Add(reader.GetString(0));
        return hosts;
    }

    public async Task<AnalyticsDashboardSnapshot> GetDashboardAsync(AnalyticsReadFilter filter, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT host, SUM(requests)::bigint, COALESCE(SUM(requests) FILTER (WHERE actor_type = 'bot'), 0)::bigint,
                   COALESCE(SUM(requests) FILTER (WHERE status_class >= 4), 0)::bigint, SUM(bytes_sent)::bigint
            FROM caddy_ui.traffic_buckets WHERE {TrafficFilter}
            GROUP BY host ORDER BY SUM(requests) DESC, host LIMIT 1000
            """;
        AddFilterParameters(command, filter);
        var domains = new List<DomainTrafficRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                domains.Add(new DomainTrafficRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        }
        var requests = domains.Sum(item => item.Requests);
        var bots = domains.Sum(item => item.BotRequests);
        var errors = domains.Sum(item => item.Errors);
        return new AnalyticsDashboardSnapshot(requests, bots, errors, domains.Sum(item => item.BytesSent), requests == 0 ? 0 : 100d * errors / requests,
            requests == 0 ? 0 : 100d * bots / requests, await GetTrafficAsync(filter, cancellationToken), domains);
    }

    public async Task<IReadOnlyList<TrafficSeriesPoint>> GetTrafficAsync(AnalyticsReadFilter filter, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT date_trunc(@interval, bucket_start), SUM(requests)::bigint,
                   COALESCE(SUM(requests) FILTER (WHERE actor_type = 'bot'), 0)::bigint, SUM(bytes_sent)::bigint,
                   COALESCE(SUM(requests) FILTER (WHERE status_class >= 4), 0)::bigint
            FROM caddy_ui.traffic_buckets WHERE {TrafficFilter}
            GROUP BY 1 ORDER BY 1 LIMIT 600
            """;
        AddFilterParameters(command, filter);
        AddParameter(command, "interval", filter.To - filter.From <= TimeSpan.FromDays(2) ? "hour" : "day");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TrafficSeriesPoint>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new TrafficSeriesPoint(ReadTimestamp(reader, 0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        return result;
    }

    public async Task<SecurityAnalyticsSnapshot> GetSecurityAsync(AnalyticsReadFilter filter, CancellationToken cancellationToken = default)
    {
        var suspicious = await GetSuspiciousClientsAsync(filter, 25, cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT (SELECT COUNT(*) FROM caddy_ui.anonymous_clients WHERE last_seen_at >= @from AND last_seen_at < @to),
                (SELECT COALESCE(SUM(requests), 0)::bigint FROM caddy_ui.traffic_buckets WHERE {TrafficFilter} AND actor_type = 'bot'),
                (SELECT COUNT(*) FROM caddy_ui.ip_block_rules WHERE enabled AND released_at IS NULL AND (expires_at IS NULL OR expires_at > CURRENT_TIMESTAMP))
            """;
        AddFilterParameters(command, filter);
        long clients, bots, blocks;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            clients = reader.GetInt64(0); bots = reader.GetInt64(1); blocks = reader.GetInt64(2);
        }
        return new SecurityAnalyticsSnapshot(clients, bots, suspicious.LongCount(item => item.RiskLevel == "high"),
            suspicious.LongCount(item => item.RiskLevel == "medium"), blocks, suspicious, await GetSecurityEventsAsync(connection, filter, cancellationToken));
    }

    public async Task<SystemAnalyticsSnapshot> GetSystemAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var connection = await OpenConnectionAsync(context, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT MAX(last_event_at), MAX(updated_at), COUNT(*)::bigint,
                    (SELECT COUNT(*)::bigint FROM caddy_ui.ingestion_failures WHERE resolved_at IS NULL),
                    COALESCE((SELECT n_live_tup FROM pg_stat_user_tables WHERE schemaname = 'caddy_ui' AND relname = 'traffic_buckets'), 0),
                    COALESCE((SELECT n_live_tup FROM pg_stat_user_tables WHERE schemaname = 'caddy_ui' AND relname = 'anonymous_clients'), 0),
                    COALESCE((SELECT (value_json ->> 'statistics_days')::integer FROM caddy_ui.application_settings WHERE key = 'analytics.retention'), @statistics_days),
                    COALESCE((SELECT (value_json ->> 'security_event_days')::integer FROM caddy_ui.application_settings WHERE key = 'analytics.retention'), @security_days)
                FROM caddy_ui.analytics_checkpoints
                """;
            AddParameter(command, "statistics_days", _analyticsOptions.StatisticsRetentionDays);
            AddParameter(command, "security_days", _analyticsOptions.SecurityEventRetentionDays);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new SystemAnalyticsSnapshot(true, string.Empty, _analyticsOptions.Enabled, _analyticsOptions.LogPaths.Count,
                reader.IsDBNull(0) ? null : ReadTimestamp(reader, 0), reader.IsDBNull(1) ? null : ReadTimestamp(reader, 1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7),
                _ipSecurityOptions.IntelligenceEnabled, _ipSecurityOptions.RiskAssessmentEnabled, _ipSecurityOptions.BlockWriteMode.ToString().ToLowerInvariant());
        }
        catch (DbException exception)
        {
            return new SystemAnalyticsSnapshot(false, exception.Message, _analyticsOptions.Enabled, _analyticsOptions.LogPaths.Count, null, null, 0, 0, 0, 0,
                _analyticsOptions.StatisticsRetentionDays, _analyticsOptions.SecurityEventRetentionDays, _ipSecurityOptions.IntelligenceEnabled,
                _ipSecurityOptions.RiskAssessmentEnabled, _ipSecurityOptions.BlockWriteMode.ToString().ToLowerInvariant());
        }
    }

    private const string TrafficFilter = """
        bucket_start >= date_bin(INTERVAL '5 minutes', @from, TIMESTAMPTZ '2000-01-01') AND bucket_start < @to
        AND (@host = '' OR host = @host) AND (@actor_type = '' OR actor_type = @actor_type)
        AND (@status_class < 0 OR status_class = @status_class)
        """;

    private static void AddFilterParameters(DbCommand command, AnalyticsReadFilter filter)
    {
        AddParameter(command, "from", filter.From); AddParameter(command, "to", filter.To);
        AddParameter(command, "host", filter.Host); AddParameter(command, "actor_type", filter.ActorType);
        AddParameter(command, "status_class", filter.StatusClass.Length == 0 ? -1 : filter.StatusClass[0] - '0');
    }

    private async Task<IReadOnlyList<SuspiciousClientRow>> GetSuspiciousClientsAsync(
        AnalyticsReadFilter filter,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT clients.id,
                   clients.client_key,
                   COALESCE((clients.metadata_json ->> 'estimated')::boolean, true),
                   clients.last_seen_at,
                   COALESCE(latest_request.remote_address, ''),
                   COALESCE(latest_assessment.classification, 'unknown'),
                   COALESCE(latest_assessment.automation_score, 0),
                   COALESCE(latest_assessment.risk, 'unknown'),
                   latest_assessment.created_at
            FROM caddy_ui.anonymous_clients AS clients
            LEFT JOIN LATERAL (
                SELECT host(requests.remote_address) AS remote_address
                FROM caddy_ui.client_activity_buckets AS requests
                WHERE requests.client_id = clients.id
                  AND requests.remote_address IS NOT NULL
                ORDER BY requests.last_seen_at DESC
                LIMIT 1
            ) AS latest_request ON true
            LEFT JOIN LATERAL (
                SELECT classification, automation_score, risk, created_at
                FROM caddy_ui.client_assessments AS assessments
                WHERE assessments.anonymous_client_id = clients.id
                ORDER BY assessments.created_at DESC
                LIMIT 1
            ) AS latest_assessment ON true
            WHERE clients.last_seen_at >= @from AND clients.last_seen_at < @to
              AND (@host = '' OR EXISTS (SELECT 1 FROM caddy_ui.client_activity_buckets AS activity
                  WHERE activity.client_id = clients.id AND activity.hosts ? @host))
            ORDER BY COALESCE(latest_assessment.automation_score, 0) DESC,
                     clients.last_seen_at DESC
            LIMIT @suspicious_limit
            """;
        AddFilterParameters(command, filter);
        AddParameter(command, "suspicious_limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SuspiciousClientRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new SuspiciousClientRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    ReadTimestamp(reader, 3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : ReadTimestamp(reader, 8)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<SecurityEventAnalyticsRow>> GetSecurityEventsAsync(
        DbConnection connection,
        AnalyticsReadFilter filter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT occurred_at, kind, reason, COALESCE(host(remote_address), ''), host, path
            FROM caddy_ui.security_events
            WHERE occurred_at >= @from
              AND occurred_at < @to
              AND (@host = '' OR host = @host)
            ORDER BY occurred_at DESC
            LIMIT 100
            """;
        AddFilterParameters(command, filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SecurityEventAnalyticsRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new SecurityEventAnalyticsRow(
                    ReadTimestamp(reader, 0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
        }

        return result;
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
}
