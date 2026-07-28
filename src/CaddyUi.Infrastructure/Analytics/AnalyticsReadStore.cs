using System.Data;
using System.Data.Common;
using System.Globalization;
using CaddyUi.Application.Analytics;
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

public sealed record TrafficSeriesPoint(
    DateTimeOffset BucketStart,
    long Requests,
    long PageViews,
    long BytesSent,
    long Errors,
    double AverageDurationMilliseconds);

public sealed record RouteAnalyticsRow(
    string Host,
    string Path,
    string RequestType,
    long RequestCount,
    long ErrorCount,
    long BytesSent,
    double AverageDurationMilliseconds,
    double MaximumDurationMilliseconds);

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

public sealed record ErrorAnalyticsRow(
    int Status,
    string Host,
    string Path,
    long Count);

public sealed record AnalyticsDashboardSnapshot(
    long Requests,
    long PageViews,
    long FailedNavigations,
    long Sessions,
    long Clients,
    long BytesSent,
    double RequestsPerPageView,
    double ErrorRatePercent,
    double P95DurationMilliseconds,
    double BotSharePercent,
    IReadOnlyList<TrafficSeriesPoint> Series,
    IReadOnlyList<RouteAnalyticsRow> TopPages,
    IReadOnlyList<RouteAnalyticsRow> TopApiRoutes,
    IReadOnlyList<RouteAnalyticsRow> LargestAssets,
    IReadOnlyList<SuspiciousClientRow> SuspiciousClients);

public sealed record SecurityAnalyticsSnapshot(
    long Clients,
    long BotRequests,
    long HighRiskClients,
    long MediumRiskClients,
    long ActiveBlocks,
    IReadOnlyList<SuspiciousClientRow> SuspiciousClients,
    IReadOnlyList<SecurityEventAnalyticsRow> RecentEvents);

public sealed record PerformanceAnalyticsSnapshot(
    long Requests,
    long Errors,
    double AverageDurationMilliseconds,
    double P50DurationMilliseconds,
    double P95DurationMilliseconds,
    double P99DurationMilliseconds,
    double MaximumDurationMilliseconds,
    IReadOnlyList<RouteAnalyticsRow> SlowRoutes,
    IReadOnlyList<ErrorAnalyticsRow> CommonErrors);

public sealed record SystemAnalyticsSnapshot(
    bool DatabaseAvailable,
    string DatabaseError,
    bool IngestionEnabled,
    int ConfiguredLogPaths,
    DateTimeOffset? LatestRequestAt,
    DateTimeOffset? LatestCheckpointAt,
    long CheckpointCount,
    long UnresolvedFailures,
    long RequestRows,
    long PageViewRows,
    long ClientRows,
    int RawRequestRetentionDays,
    int PageViewRetentionDays,
    bool IpIntelligenceEnabled,
    bool RiskAssessmentEnabled,
    string BlockWriteMode);

public sealed class AnalyticsReadStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly AnalyticsIngestionOptions _analyticsOptions;
    private readonly IpSecurityOptions _ipSecurityOptions;

    public AnalyticsReadStore(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        AnalyticsIngestionOptions analyticsOptions,
        IpSecurityOptions ipSecurityOptions)
    {
        _contextFactory = contextFactory;
        _analyticsOptions = analyticsOptions;
        _ipSecurityOptions = ipSecurityOptions;
    }

    public async Task<IReadOnlyList<string>> ListHostsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT host
            FROM caddy_ui.request_events
            WHERE host <> ''
            ORDER BY host
            LIMIT 500
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task<AnalyticsDashboardSnapshot> GetDashboardAsync(
        AnalyticsReadFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);

        long requests;
        long bytesSent;
        long errors;
        long botRequests;
        double p95;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT COUNT(*)::bigint,
                       COALESCE(SUM(requests.bytes_sent), 0)::bigint,
                       COUNT(*) FILTER (WHERE requests.status >= 400)::bigint,
                       COUNT(*) FILTER (WHERE requests.actor_type = 'bot')::bigint,
                       COALESCE(
                           percentile_cont(0.95) WITHIN GROUP (ORDER BY requests.duration_ms),
                           0)::double precision
                FROM caddy_ui.request_events AS requests
                WHERE {RequestFilterSql("requests")}
                """;
            AddFilterParameters(command, filter);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            requests = reader.GetInt64(0);
            bytesSent = reader.GetInt64(1);
            errors = reader.GetInt64(2);
            botRequests = reader.GetInt64(3);
            p95 = reader.GetDouble(4);
        }

        var includePageViews = IncludesPageViews(filter);
        var pageViews = await CountPageViewsAsync(connection, filter, includePageViews, cancellationToken);
        var failedNavigations = await CountFailedNavigationsAsync(
            connection,
            filter,
            includePageViews,
            cancellationToken);
        var sessions = await CountSessionsAsync(connection, filter, cancellationToken);
        var clients = await CountClientsAsync(connection, filter, cancellationToken);
        var series = await GetTrafficAsync(filter, cancellationToken);
        var topPages = await GetTopPagesAsync(filter, 10, cancellationToken);
        var routes = await GetRouteAnalyticsAsync(filter with { Limit = 250 }, cancellationToken);
        var topApiRoutes = routes
            .Where(item => item.RequestType == "api")
            .OrderByDescending(item => item.RequestCount)
            .Take(10)
            .ToArray();
        var largestAssets = routes
            .Where(item => item.RequestType == "asset")
            .OrderByDescending(item => item.BytesSent)
            .Take(10)
            .ToArray();
        var suspiciousClients = await GetSuspiciousClientsAsync(filter, 8, cancellationToken);

        return new AnalyticsDashboardSnapshot(
            requests,
            pageViews,
            failedNavigations,
            sessions,
            clients,
            bytesSent,
            pageViews == 0 ? 0 : requests / (double)pageViews,
            requests == 0 ? 0 : errors * 100d / requests,
            p95,
            requests == 0 ? 0 : botRequests * 100d / requests,
            series,
            topPages,
            topApiRoutes,
            largestAssets,
            suspiciousClients);
    }

    public async Task<IReadOnlyList<TrafficSeriesPoint>> GetTrafficAsync(
        AnalyticsReadFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var bucket = filter.To - filter.From <= TimeSpan.FromHours(48) ? "hour" : "day";
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            WITH request_series AS (
                SELECT date_trunc(@bucket, requests.occurred_at) AS bucket_start,
                       COUNT(*)::bigint AS requests,
                       COALESCE(SUM(requests.bytes_sent), 0)::bigint AS bytes_sent,
                       COUNT(*) FILTER (WHERE requests.status >= 400)::bigint AS errors,
                       COALESCE(AVG(requests.duration_ms), 0)::double precision AS average_duration
                FROM caddy_ui.request_events AS requests
                WHERE {RequestFilterSql("requests")}
                GROUP BY date_trunc(@bucket, requests.occurred_at)
            ), page_view_series AS (
                SELECT date_trunc(@bucket, views.occurred_at) AS bucket_start,
                       COUNT(*)::bigint AS page_views
                FROM caddy_ui.page_views AS views
                WHERE views.occurred_at >= @from
                  AND views.occurred_at < @to
                  AND (@host = '' OR views.host = @host)
                  AND @include_page_views
                GROUP BY date_trunc(@bucket, views.occurred_at)
            )
            SELECT COALESCE(request_series.bucket_start, page_view_series.bucket_start),
                   COALESCE(request_series.requests, 0),
                   COALESCE(page_view_series.page_views, 0),
                   COALESCE(request_series.bytes_sent, 0),
                   COALESCE(request_series.errors, 0),
                   COALESCE(request_series.average_duration, 0)
            FROM request_series
            FULL OUTER JOIN page_view_series USING (bucket_start)
            ORDER BY COALESCE(request_series.bucket_start, page_view_series.bucket_start)
            """;
        AddFilterParameters(command, filter);
        AddParameter(command, "bucket", bucket);
        AddParameter(command, "include_page_views", IncludesPageViews(filter));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TrafficSeriesPoint>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new TrafficSeriesPoint(
                    ReadTimestamp(reader, 0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetDouble(5)));
        }

        return result;
    }

    public async Task<IReadOnlyList<RouteAnalyticsRow>> GetTopPagesAsync(
        AnalyticsReadFilter filter,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!IncludesPageViews(filter))
        {
            return Array.Empty<RouteAnalyticsRow>();
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT views.host, views.path, COUNT(*)::bigint
            FROM caddy_ui.page_views AS views
            WHERE views.occurred_at >= @from
              AND views.occurred_at < @to
              AND (@host = '' OR views.host = @host)
            GROUP BY views.host, views.path
            ORDER BY COUNT(*) DESC, views.host, views.path
            LIMIT @limit
            """;
        AddFilterParameters(command, filter);
        AddParameter(command, "limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RouteAnalyticsRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new RouteAnalyticsRow(
                    reader.GetString(0),
                    PathCardinalityNormalizer.Normalize(reader.GetString(1)),
                    "document",
                    reader.GetInt64(2),
                    0,
                    0,
                    0,
                    0));
        }

        return result;
    }

    public async Task<IReadOnlyList<RouteAnalyticsRow>> GetRouteAnalyticsAsync(
        AnalyticsReadFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT requests.host,
                   requests.path,
                   requests.request_type,
                   COUNT(*)::bigint,
                   COUNT(*) FILTER (WHERE requests.status >= 400)::bigint,
                   COALESCE(SUM(requests.bytes_sent), 0)::bigint,
                   COALESCE(AVG(requests.duration_ms), 0)::double precision,
                   COALESCE(MAX(requests.duration_ms), 0)::double precision
            FROM caddy_ui.request_events AS requests
            WHERE {RequestFilterSql("requests")}
            GROUP BY requests.host, requests.path, requests.request_type
            ORDER BY COUNT(*) DESC
            LIMIT 2000
            """;
        AddFilterParameters(command, filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var raw = new List<RouteAnalyticsRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            raw.Add(
                new RouteAnalyticsRow(
                    reader.GetString(0),
                    PathCardinalityNormalizer.Normalize(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7)));
        }

        return raw
            .GroupBy(item => (item.Host, item.Path, item.RequestType))
            .Select(group =>
            {
                var requestCount = group.Sum(item => item.RequestCount);
                return new RouteAnalyticsRow(
                    group.Key.Host,
                    group.Key.Path,
                    group.Key.RequestType,
                    requestCount,
                    group.Sum(item => item.ErrorCount),
                    group.Sum(item => item.BytesSent),
                    requestCount == 0
                        ? 0
                        : group.Sum(item => item.AverageDurationMilliseconds * item.RequestCount) /
                          requestCount,
                    group.Max(item => item.MaximumDurationMilliseconds));
            })
            .OrderByDescending(item => item.RequestCount)
            .ThenBy(item => item.Host, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .Take(filter.Limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<RequestAnalyticsRow>> GetRequestsAsync(
        AnalyticsReadFilter filter,
        DateTimeOffset? after = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT requests.occurred_at,
                   requests.host,
                   requests.method,
                   requests.path,
                   requests.status,
                   requests.request_type,
                   requests.actor_type,
                   requests.duration_ms,
                   requests.bytes_sent,
                   COALESCE(host(requests.remote_address), ''),
                   COALESCE(requests.anonymous_client_id::text, '')
            FROM caddy_ui.request_events AS requests
            WHERE {RequestFilterSql("requests")}
              AND (@after IS NULL OR requests.occurred_at > @after)
            ORDER BY requests.occurred_at DESC
            LIMIT @limit
            """;
        AddFilterParameters(command, filter);
        AddParameter(command, "after", after);
        AddParameter(command, "limit", filter.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RequestAnalyticsRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new RequestAnalyticsRow(
                    ReadTimestamp(reader, 0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetDouble(7),
                    reader.GetInt64(8),
                    reader.GetString(9),
                    reader.GetString(10)));
        }

        return result;
    }

    public async Task<SecurityAnalyticsSnapshot> GetSecurityAsync(
        AnalyticsReadFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var clients = await CountClientsAsync(
            await OpenNewConnectionAsync(cancellationToken),
            filter,
            cancellationToken);
        var suspiciousClients = await GetSuspiciousClientsAsync(filter, 25, cancellationToken);
        var highRisk = suspiciousClients.LongCount(item => item.RiskLevel == "high");
        var mediumRisk = suspiciousClients.LongCount(item => item.RiskLevel == "medium");

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        long botRequests;
        long activeBlocks;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT COUNT(*) FILTER (WHERE requests.actor_type = 'bot')::bigint
                FROM caddy_ui.request_events AS requests
                WHERE {RequestFilterSql("requests")}
                """;
            AddFilterParameters(command, filter);
            botRequests = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COUNT(*)::bigint
                FROM caddy_ui.ip_block_rules
                WHERE enabled = true
                  AND released_at IS NULL
                  AND (expires_at IS NULL OR expires_at > CURRENT_TIMESTAMP)
                """;
            activeBlocks = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        var events = await GetSecurityEventsAsync(connection, filter, cancellationToken);
        return new SecurityAnalyticsSnapshot(
            clients,
            botRequests,
            highRisk,
            mediumRisk,
            activeBlocks,
            suspiciousClients,
            events);
    }

    public async Task<PerformanceAnalyticsSnapshot> GetPerformanceAsync(
        AnalyticsReadFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        long requests;
        long errors;
        double average;
        double p50;
        double p95;
        double p99;
        double maximum;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT COUNT(*)::bigint,
                       COUNT(*) FILTER (WHERE requests.status >= 400)::bigint,
                       COALESCE(AVG(requests.duration_ms), 0)::double precision,
                       COALESCE(percentile_cont(0.50) WITHIN GROUP (ORDER BY requests.duration_ms), 0)::double precision,
                       COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY requests.duration_ms), 0)::double precision,
                       COALESCE(percentile_cont(0.99) WITHIN GROUP (ORDER BY requests.duration_ms), 0)::double precision,
                       COALESCE(MAX(requests.duration_ms), 0)::double precision
                FROM caddy_ui.request_events AS requests
                WHERE {RequestFilterSql("requests")}
                """;
            AddFilterParameters(command, filter);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            requests = reader.GetInt64(0);
            errors = reader.GetInt64(1);
            average = reader.GetDouble(2);
            p50 = reader.GetDouble(3);
            p95 = reader.GetDouble(4);
            p99 = reader.GetDouble(5);
            maximum = reader.GetDouble(6);
        }

        var routes = await GetRouteAnalyticsAsync(filter with { Limit = 500 }, cancellationToken);
        var slowRoutes = routes
            .OrderByDescending(item => item.AverageDurationMilliseconds)
            .ThenByDescending(item => item.MaximumDurationMilliseconds)
            .Take(25)
            .ToArray();
        var commonErrors = await GetCommonErrorsAsync(connection, filter, cancellationToken);
        return new PerformanceAnalyticsSnapshot(
            requests,
            errors,
            average,
            p50,
            p95,
            p99,
            maximum,
            slowRoutes,
            commonErrors);
    }

    public async Task<SystemAnalyticsSnapshot> GetSystemAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var connection = await OpenConnectionAsync(context, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT (SELECT MAX(occurred_at) FROM caddy_ui.request_events),
                       (SELECT MAX(updated_at) FROM caddy_ui.analytics_checkpoints),
                       (SELECT COUNT(*)::bigint FROM caddy_ui.analytics_checkpoints),
                       (SELECT COUNT(*)::bigint FROM caddy_ui.ingestion_failures WHERE resolved_at IS NULL),
                       (SELECT COUNT(*)::bigint FROM caddy_ui.request_events),
                       (SELECT COUNT(*)::bigint FROM caddy_ui.page_views),
                       (SELECT COUNT(*)::bigint FROM caddy_ui.anonymous_clients)
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new SystemAnalyticsSnapshot(
                true,
                string.Empty,
                _analyticsOptions.Enabled,
                _analyticsOptions.LogPaths.Count,
                reader.IsDBNull(0) ? null : ReadTimestamp(reader, 0),
                reader.IsDBNull(1) ? null : ReadTimestamp(reader, 1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                _analyticsOptions.RawRequestRetentionDays,
                _analyticsOptions.PageViewRetentionDays,
                _ipSecurityOptions.IntelligenceEnabled,
                _ipSecurityOptions.RiskAssessmentEnabled,
                _ipSecurityOptions.BlockWriteMode.ToString().ToLowerInvariant());
        }
        catch (DbException exception)
        {
            return new SystemAnalyticsSnapshot(
                false,
                exception.Message,
                _analyticsOptions.Enabled,
                _analyticsOptions.LogPaths.Count,
                null,
                null,
                0,
                0,
                0,
                0,
                0,
                _analyticsOptions.RawRequestRetentionDays,
                _analyticsOptions.PageViewRetentionDays,
                _ipSecurityOptions.IntelligenceEnabled,
                _ipSecurityOptions.RiskAssessmentEnabled,
                _ipSecurityOptions.BlockWriteMode.ToString().ToLowerInvariant());
        }
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
            $"""
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
                FROM caddy_ui.request_events AS requests
                WHERE requests.anonymous_client_id = clients.id
                  AND {RequestFilterSql("requests")}
                  AND requests.remote_address IS NOT NULL
                ORDER BY requests.occurred_at DESC
                LIMIT 1
            ) AS latest_request ON true
            LEFT JOIN LATERAL (
                SELECT classification, automation_score, risk, created_at
                FROM caddy_ui.client_assessments AS assessments
                WHERE assessments.anonymous_client_id = clients.id
                ORDER BY assessments.created_at DESC
                LIMIT 1
            ) AS latest_assessment ON true
            WHERE EXISTS (
                SELECT 1
                FROM caddy_ui.request_events AS matching_requests
                WHERE matching_requests.anonymous_client_id = clients.id
                  AND {RequestFilterSql("matching_requests")})
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

    private static async Task<IReadOnlyList<ErrorAnalyticsRow>> GetCommonErrorsAsync(
        DbConnection connection,
        AnalyticsReadFilter filter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT requests.status, requests.host, requests.path, COUNT(*)::bigint
            FROM caddy_ui.request_events AS requests
            WHERE {RequestFilterSql("requests")}
              AND requests.status >= 400
            GROUP BY requests.status, requests.host, requests.path
            ORDER BY COUNT(*) DESC
            LIMIT 500
            """;
        AddFilterParameters(command, filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var raw = new List<ErrorAnalyticsRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            raw.Add(
                new ErrorAnalyticsRow(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    PathCardinalityNormalizer.Normalize(reader.GetString(2)),
                    reader.GetInt64(3)));
        }

        return raw
            .GroupBy(item => (item.Status, item.Host, item.Path))
            .Select(group => new ErrorAnalyticsRow(
                group.Key.Status,
                group.Key.Host,
                group.Key.Path,
                group.Sum(item => item.Count)))
            .OrderByDescending(item => item.Count)
            .Take(50)
            .ToArray();
    }

    private static async Task<long> CountPageViewsAsync(
        DbConnection connection,
        AnalyticsReadFilter filter,
        bool includePageViews,
        CancellationToken cancellationToken)
    {
        if (!includePageViews)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)::bigint
            FROM caddy_ui.page_views
            WHERE occurred_at >= @from
              AND occurred_at < @to
              AND (@host = '' OR host = @host)
            """;
        AddFilterParameters(command, filter);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountFailedNavigationsAsync(
        DbConnection connection,
        AnalyticsReadFilter filter,
        bool includePageViews,
        CancellationToken cancellationToken)
    {
        if (!includePageViews)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)::bigint
            FROM caddy_ui.navigation_events
            WHERE occurred_at >= @from
              AND occurred_at < @to
              AND state = 'failed'
              AND (@host = '' OR host = @host)
            """;
        AddFilterParameters(command, filter);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountSessionsAsync(
        DbConnection connection,
        AnalyticsReadFilter filter,
        CancellationToken cancellationToken)
    {
        if (filter.ActorType is "bot" or "internal" ||
            (filter.RequestType.Length > 0 && filter.RequestType != "document"))
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)::bigint
            FROM caddy_ui.analytics_sessions
            WHERE started_at < @to
              AND last_activity_at >= @from
              AND (@host = '' OR host = @host)
            """;
        AddFilterParameters(command, filter);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountClientsAsync(
        DbConnection connection,
        AnalyticsReadFilter filter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COUNT(DISTINCT requests.anonymous_client_id)::bigint
            FROM caddy_ui.request_events AS requests
            WHERE {RequestFilterSql("requests")}
              AND requests.anonymous_client_id IS NOT NULL
            """;
        AddFilterParameters(command, filter);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private async Task<DbConnection> OpenNewConnectionAsync(CancellationToken cancellationToken)
    {
        var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await OpenConnectionAsync(context, cancellationToken);
    }

    private static string RequestFilterSql(string alias)
    {
        return $"""
            {alias}.occurred_at >= @from
            AND {alias}.occurred_at < @to
            AND (@host = '' OR {alias}.host = @host)
            AND (@actor_type = '' OR {alias}.actor_type = @actor_type)
            AND (@request_type = '' OR {alias}.request_type = @request_type)
            AND (@status_min < 0 OR ({alias}.status >= @status_min AND {alias}.status <= @status_max))
            """;
    }

    private static bool IncludesPageViews(AnalyticsReadFilter filter)
    {
        return (filter.ActorType.Length == 0 || filter.ActorType == "human") &&
               (filter.RequestType.Length == 0 || filter.RequestType == "document") &&
               (filter.StatusClass.Length == 0 || filter.StatusClass == "2xx" || filter.StatusClass == "3xx");
    }

    private static void AddFilterParameters(DbCommand command, AnalyticsReadFilter filter)
    {
        AddParameter(command, "from", filter.From);
        AddParameter(command, "to", filter.To);
        AddParameter(command, "host", filter.Host);
        AddParameter(command, "actor_type", filter.ActorType);
        AddParameter(command, "request_type", filter.RequestType);
        var statusMin = -1;
        var statusMax = -1;
        if (filter.StatusClass.Length == 3 &&
            int.TryParse(filter.StatusClass.AsSpan(0, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var statusClass))
        {
            statusMin = statusClass * 100;
            statusMax = statusMin + 99;
        }

        AddParameter(command, "status_min", statusMin);
        AddParameter(command, "status_max", statusMax);
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
