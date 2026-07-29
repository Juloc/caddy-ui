using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CaddyUi.Application.Analytics;
using CaddyUi.Domain.Analytics;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Analytics;

public sealed record AnalyticsIngestionFailure(
    long? SourceOffset,
    string SafeRawLine,
    string Error);

public sealed record AnalyticsPersistResult(
    int RequestsInserted,
    int PageViewsInserted,
    int FailuresInserted);

public sealed class AnalyticsIngestionStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public AnalyticsIngestionStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<AnalyticsCheckpoint?> GetCheckpointAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source, source_identity, byte_offset, last_event_at
            FROM caddy_ui.analytics_checkpoints
            WHERE source = @source
            """;
        AddParameter(command, "source", source);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AnalyticsCheckpoint(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : ReadTimestamp(reader, 3));
    }

    public async Task<AnalyticsPersistResult> PersistBatchAsync(
        string source,
        string sourceIdentity,
        long byteOffset,
        IReadOnlyList<ClassifiedRequest> requests,
        IReadOnlyList<AnalyticsIngestionFailure> failures,
        byte[] clientHashKey,
        AnalyticsIngestionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(clientHashKey);
        ArgumentNullException.ThrowIfNull(options);
        if (clientHashKey.Length == 0)
        {
            throw new ArgumentException(
                "The analytics client hash key must not be empty.",
                nameof(clientHashKey));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var month in requests
                         .Select(item => new DateOnly(
                             item.Request.OccurredAt.Year,
                             item.Request.OccurredAt.Month,
                             1))
                         .Distinct())
            {
                await EnsurePartitionAsync(connection, transaction, month, cancellationToken);
            }

            var requestsInserted = 0;
            var pageViewsInserted = 0;
            DateTimeOffset? lastEventAt = null;

            foreach (var classified in requests.OrderBy(item => item.Request.OccurredAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = classified.Request;
                lastEventAt = lastEventAt is null || request.OccurredAt > lastEventAt.Value
                    ? request.OccurredAt
                    : lastEventAt;

                var identity = classified.ActorType == AnalyticsActorType.Internal
                    ? null
                    : AnalyticsClientFingerprint.Create(request, clientHashKey);
                Guid? clientId = identity is null
                    ? null
                    : await GetOrCreateClientAsync(
                        connection,
                        transaction,
                        identity,
                        request.OccurredAt,
                        cancellationToken);
                Guid? sessionId = clientId is null ||
                    classified.ActorType is AnalyticsActorType.Bot or AnalyticsActorType.Internal
                        ? null
                        : await FindOrCreateSessionAsync(
                            connection,
                            transaction,
                            clientId.Value,
                            request.Host,
                            request.OccurredAt,
                            TimeSpan.FromMinutes(options.SessionIdleMinutes),
                            classified.IsNavigation,
                            cancellationToken);

                if (!await InsertRequestAsync(
                        connection,
                        transaction,
                        classified,
                        clientId,
                        cancellationToken))
                {
                    continue;
                }

                requestsInserted++;
                if (sessionId is not null)
                {
                    await UpdateSessionAsync(
                        connection,
                        transaction,
                        sessionId.Value,
                        request.OccurredAt,
                        cancellationToken);
                }

                if (classified.IsNavigation)
                {
                    var navigationId = await InsertNavigationAsync(
                        connection,
                        transaction,
                        classified,
                        sessionId,
                        cancellationToken);
                    if (classified.IsPageView)
                    {
                        await InsertPageViewAsync(
                            connection,
                            transaction,
                            classified,
                            navigationId,
                            sessionId,
                            clientId,
                            options.PageLoadWindowSeconds,
                            cancellationToken);
                        pageViewsInserted++;
                    }
                }
                else if (clientId is not null)
                {
                    await AddToRecentPageLoadAsync(
                        connection,
                        transaction,
                        classified,
                        clientId.Value,
                        TimeSpan.FromSeconds(options.PageLoadWindowSeconds),
                        cancellationToken);
                }

                await UpsertAggregatesAsync(
                    connection,
                    transaction,
                    classified,
                    cancellationToken);
            }

            foreach (var failure in failures)
            {
                await InsertFailureAsync(
                    connection,
                    transaction,
                    source,
                    failure,
                    cancellationToken);
            }

            await UpsertCheckpointAsync(
                connection,
                transaction,
                source,
                sourceIdentity,
                byteOffset,
                lastEventAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new AnalyticsPersistResult(
                requestsInserted,
                pageViewsInserted,
                failures.Count);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task RunMaintenanceAsync(
        AnalyticsIngestionOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE caddy_ui.analytics_sessions
                SET ended_at = last_activity_at
                WHERE ended_at IS NULL
                  AND last_activity_at < @session_cutoff;

                UPDATE caddy_ui.page_loads
                SET completed_at = COALESCE(completed_at, started_at)
                WHERE completed_at IS NULL
                  AND started_at < @page_load_cutoff;

                DELETE FROM caddy_ui.request_events
                WHERE occurred_at < @raw_cutoff;

                DELETE FROM caddy_ui.navigation_events
                WHERE occurred_at < @pageview_cutoff;

                DELETE FROM caddy_ui.hourly_traffic_aggregates
                WHERE bucket_start < @hourly_cutoff;

                DELETE FROM caddy_ui.daily_traffic_aggregates
                WHERE bucket_start < @daily_cutoff;

                DELETE FROM caddy_ui.analytics_sessions
                WHERE COALESCE(ended_at, last_activity_at) < @pageview_cutoff;
                """,
                command =>
                {
                    AddParameter(
                        command,
                        "session_cutoff",
                        now.Subtract(TimeSpan.FromMinutes(options.SessionIdleMinutes)));
                    AddParameter(
                        command,
                        "page_load_cutoff",
                        now.Subtract(TimeSpan.FromSeconds(options.PageLoadWindowSeconds)));
                    AddParameter(command, "raw_cutoff", now.AddDays(-options.RawRequestRetentionDays));
                    AddParameter(command, "pageview_cutoff", now.AddDays(-options.PageViewRetentionDays));
                    AddParameter(command, "hourly_cutoff", now.AddDays(-options.HourlyRetentionDays));
                    AddParameter(
                        command,
                        "daily_cutoff",
                        DateOnly.FromDateTime(now.AddDays(-options.DailyRetentionDays).UtcDateTime));
                },
                cancellationToken);

            await ExecuteAsync(
                connection,
                transaction,
                "SELECT caddy_ui.drop_expired_request_event_partitions(@cutoff)",
                command => AddParameter(
                    command,
                    "cutoff",
                    DateOnly.FromDateTime(
                        now.AddDays(-options.RawRequestRetentionDays).UtcDateTime)),
                cancellationToken);

            await EnsurePartitionAsync(
                connection,
                transaction,
                new DateOnly(now.Year, now.Month, 1),
                cancellationToken);
            var nextMonth = now.AddMonths(1);
            await EnsurePartitionAsync(
                connection,
                transaction,
                new DateOnly(nextMonth.Year, nextMonth.Month, 1),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static Task EnsurePartitionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateOnly month,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            connection,
            transaction,
            "SELECT caddy_ui.ensure_request_event_partition(@month)",
            command => AddParameter(command, "month", month),
            cancellationToken);
    }

    private static async Task<Guid> GetOrCreateClientAsync(
        DbConnection connection,
        DbTransaction transaction,
        AnalyticsClientIdentity identity,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO caddy_ui.anonymous_clients(
                id, client_key, first_seen_at, last_seen_at,
                first_party_identifier_hash, metadata_json)
            VALUES(
                @id, @client_key, @occurred_at, @occurred_at,
                @first_party_identifier_hash, CAST(@metadata_json AS jsonb))
            ON CONFLICT (client_key)
            DO UPDATE SET
                last_seen_at = GREATEST(
                    caddy_ui.anonymous_clients.last_seen_at,
                    EXCLUDED.last_seen_at),
                first_party_identifier_hash = COALESCE(
                    caddy_ui.anonymous_clients.first_party_identifier_hash,
                    EXCLUDED.first_party_identifier_hash),
                metadata_json = caddy_ui.anonymous_clients.metadata_json || EXCLUDED.metadata_json
            RETURNING id
            """;
        AddParameter(command, "id", Guid.NewGuid());
        AddParameter(command, "client_key", identity.ClientKey);
        AddParameter(command, "occurred_at", occurredAt);
        AddParameter(command, "first_party_identifier_hash", identity.FirstPartyIdentifierHash);
        AddParameter(
            command,
            "metadata_json",
            JsonSerializer.Serialize(new Dictionary<string, bool>
            {
                ["estimated"] = identity.Estimated,
            }));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("The analytics client could not be created."));
    }

    private static async Task<Guid?> FindOrCreateSessionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid clientId,
        string host,
        DateTimeOffset occurredAt,
        TimeSpan idleTimeout,
        bool createWhenMissing,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT id
                FROM caddy_ui.analytics_sessions
                WHERE anonymous_client_id = @client_id
                  AND host = @host
                  AND ended_at IS NULL
                  AND last_activity_at >= @activity_cutoff
                  AND last_activity_at <= @future_cutoff
                ORDER BY last_activity_at DESC
                LIMIT 1
                FOR UPDATE
                """;
            AddParameter(command, "client_id", clientId);
            AddParameter(command, "host", host);
            AddParameter(command, "activity_cutoff", occurredAt.Subtract(idleTimeout));
            AddParameter(command, "future_cutoff", occurredAt.Add(idleTimeout));
            if (await command.ExecuteScalarAsync(cancellationToken) is Guid existing)
            {
                return existing;
            }
        }

        if (!createWhenMissing)
        {
            return null;
        }

        var sessionId = Guid.NewGuid();
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.analytics_sessions(
                id, anonymous_client_id, host, started_at, last_activity_at,
                ended_at, page_view_count, request_count)
            VALUES(
                @id, @client_id, @host, @occurred_at, @occurred_at,
                NULL, 0, 0)
            """,
            command =>
            {
                AddParameter(command, "id", sessionId);
                AddParameter(command, "client_id", clientId);
                AddParameter(command, "host", host);
                AddParameter(command, "occurred_at", occurredAt);
            },
            cancellationToken);
        return sessionId;
    }

    private static async Task<bool> InsertRequestAsync(
        DbConnection connection,
        DbTransaction transaction,
        ClassifiedRequest classified,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        var request = classified.Request;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO caddy_ui.request_events(
                id, occurred_at, source_file, source_offset, host, method, path,
                query_string, status, duration_ms, bytes_sent, remote_address,
                user_agent, referer, accept_header, content_type, sec_fetch_dest,
                actor_type, request_type, classification_confidence,
                managed_route_id, anonymous_client_id, raw_json)
            VALUES(
                @id, @occurred_at, @source_file, @source_offset, @host, @method, @path,
                @query_string, @status, @duration_ms, @bytes_sent,
                CAST(@remote_address AS inet), @user_agent, @referer, @accept_header,
                @content_type, @sec_fetch_dest, @actor_type, @request_type,
                @classification_confidence, NULL, @anonymous_client_id,
                CAST(@raw_json AS jsonb))
            ON CONFLICT DO NOTHING
            RETURNING id
            """;
        AddParameter(command, "id", request.Id);
        AddParameter(command, "occurred_at", request.OccurredAt);
        AddParameter(command, "source_file", request.SourceFile);
        AddParameter(command, "source_offset", request.SourceOffset);
        AddParameter(command, "host", request.Host);
        AddParameter(command, "method", request.Method);
        AddParameter(command, "path", request.Path);
        AddParameter(command, "query_string", request.QueryString);
        AddParameter(command, "status", request.Status);
        AddParameter(command, "duration_ms", request.DurationMilliseconds);
        AddParameter(command, "bytes_sent", request.BytesSent);
        AddParameter(command, "remote_address", request.RemoteAddress);
        AddParameter(command, "user_agent", request.UserAgent);
        AddParameter(command, "referer", request.Referer);
        AddParameter(command, "accept_header", request.AcceptHeader);
        AddParameter(command, "content_type", request.ContentType);
        AddParameter(command, "sec_fetch_dest", request.SecFetchDest);
        AddParameter(command, "actor_type", classified.ActorType.ToStorageValue());
        AddParameter(command, "request_type", classified.RequestType.ToStorageValue());
        AddParameter(
            command,
            "classification_confidence",
            classified.Confidence.ToStorageValue());
        AddParameter(command, "anonymous_client_id", clientId);
        AddParameter(command, "raw_json", request.RawJson);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static Task UpdateSessionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid sessionId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE caddy_ui.analytics_sessions
            SET last_activity_at = GREATEST(last_activity_at, @occurred_at),
                request_count = request_count + 1
            WHERE id = @id
            """,
            command =>
            {
                AddParameter(command, "occurred_at", occurredAt);
                AddParameter(command, "id", sessionId);
            },
            cancellationToken);
    }

    private static async Task<Guid> InsertNavigationAsync(
        DbConnection connection,
        DbTransaction transaction,
        ClassifiedRequest classified,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        var request = classified.Request;
        var navigationId = Guid.NewGuid();
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.navigation_events(
                id, request_event_id, request_occurred_at, analytics_session_id,
                occurred_at, host, path, state, evidence_json)
            VALUES(
                @id, @request_event_id, @request_occurred_at, @session_id,
                @occurred_at, @host, @path, @state, CAST(@evidence_json AS jsonb))
            """,
            command =>
            {
                AddParameter(command, "id", navigationId);
                AddParameter(command, "request_event_id", request.Id);
                AddParameter(command, "request_occurred_at", request.OccurredAt);
                AddParameter(command, "session_id", sessionId);
                AddParameter(command, "occurred_at", request.OccurredAt);
                AddParameter(command, "host", request.Host);
                AddParameter(command, "path", request.Path);
                AddParameter(command, "state", classified.NavigationState.ToStorageValue());
                AddParameter(command, "evidence_json", JsonSerializer.Serialize(classified.Evidence));
            },
            cancellationToken);
        return navigationId;
    }

    private static Task InsertPageViewAsync(
        DbConnection connection,
        DbTransaction transaction,
        ClassifiedRequest classified,
        Guid navigationId,
        Guid? sessionId,
        Guid? clientId,
        int pageLoadWindowSeconds,
        CancellationToken cancellationToken)
    {
        var request = classified.Request;
        var pageViewId = Guid.NewGuid();
        return ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.page_views(
                id, navigation_event_id, analytics_session_id, anonymous_client_id,
                occurred_at, host, path, source, successful, estimated)
            VALUES(
                @id, @navigation_id, @session_id, @client_id,
                @occurred_at, @host, @path, @source, true, @estimated);

            INSERT INTO caddy_ui.page_loads(
                id, page_view_id, started_at, completed_at, request_count,
                asset_request_count, api_request_count, bytes_sent, estimated,
                grouping_evidence_json)
            VALUES(
                @load_id, @id, @occurred_at, NULL, 1, 0, 0, @bytes_sent,
                @estimated, CAST(@grouping_evidence_json AS jsonb));

            UPDATE caddy_ui.analytics_sessions
            SET page_view_count = page_view_count + 1
            WHERE id = @session_id;
            """,
            command =>
            {
                AddParameter(command, "id", pageViewId);
                AddParameter(command, "load_id", Guid.NewGuid());
                AddParameter(command, "navigation_id", navigationId);
                AddParameter(command, "session_id", sessionId);
                AddParameter(command, "client_id", clientId);
                AddParameter(command, "occurred_at", request.OccurredAt);
                AddParameter(command, "host", request.Host);
                AddParameter(command, "path", request.Path);
                AddParameter(
                    command,
                    "source",
                    string.Equals(
                        request.SecFetchDest,
                        "document",
                        StringComparison.OrdinalIgnoreCase)
                            ? "sec-fetch-document"
                            : "proxy-estimate");
                AddParameter(
                    command,
                    "estimated",
                    string.IsNullOrWhiteSpace(request.FirstPartyClientIdentifier));
                AddParameter(command, "bytes_sent", request.BytesSent);
                AddParameter(
                    command,
                    "grouping_evidence_json",
                    JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["windowSeconds"] = pageLoadWindowSeconds,
                        ["evidence"] = classified.Evidence,
                    }));
            },
            cancellationToken);
    }

    private static Task AddToRecentPageLoadAsync(
        DbConnection connection,
        DbTransaction transaction,
        ClassifiedRequest classified,
        Guid clientId,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var request = classified.Request;
        return ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE caddy_ui.page_loads
            SET request_count = request_count + 1,
                asset_request_count = asset_request_count + @asset_increment,
                api_request_count = api_request_count + @api_increment,
                bytes_sent = bytes_sent + @bytes_sent,
                completed_at = GREATEST(
                    COALESCE(completed_at, @occurred_at),
                    @occurred_at)
            WHERE id = (
                SELECT loads.id
                FROM caddy_ui.page_loads AS loads
                JOIN caddy_ui.page_views AS views
                  ON views.id = loads.page_view_id
                WHERE views.anonymous_client_id = @client_id
                  AND views.host = @host
                  AND loads.started_at <= @occurred_at
                  AND loads.started_at >= @window_start
                ORDER BY loads.started_at DESC
                LIMIT 1
            )
            """,
            command =>
            {
                AddParameter(
                    command,
                    "asset_increment",
                    classified.RequestType == AnalyticsRequestType.Asset ? 1 : 0);
                AddParameter(
                    command,
                    "api_increment",
                    classified.RequestType == AnalyticsRequestType.Api ? 1 : 0);
                AddParameter(command, "bytes_sent", request.BytesSent);
                AddParameter(command, "occurred_at", request.OccurredAt);
                AddParameter(command, "client_id", clientId);
                AddParameter(command, "host", request.Host);
                AddParameter(command, "window_start", request.OccurredAt.Subtract(window));
            },
            cancellationToken);
    }

    private static Task UpsertAggregatesAsync(
        DbConnection connection,
        DbTransaction transaction,
        ClassifiedRequest classified,
        CancellationToken cancellationToken)
    {
        var request = classified.Request;
        var utc = request.OccurredAt.ToUniversalTime();
        var hour = new DateTimeOffset(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            0,
            0,
            TimeSpan.Zero);
        var day = new DateOnly(utc.Year, utc.Month, utc.Day);
        var month = new DateOnly(utc.Year, utc.Month, 1);
        var pageViews = classified.IsPageView ? 1 : 0;
        var statusClass = request.Status is >= 100 and <= 599
            ? string.Create(CultureInfo.InvariantCulture, $"{request.Status / 100}xx")
            : "other";
        var pathPattern = PathCardinalityNormalizer.Normalize(request.Path);

        return ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.hourly_traffic_aggregates(
                bucket_start, host, status_class, actor_type, request_type,
                requests, page_views, bytes_sent, duration_sum_ms, duration_max_ms)
            VALUES(
                @hour, @host, @status_class, @actor_type, @request_type,
                1, @page_views, @bytes_sent, @duration_ms, @duration_ms)
            ON CONFLICT (bucket_start, host, status_class, actor_type, request_type)
            DO UPDATE SET
                requests = caddy_ui.hourly_traffic_aggregates.requests + 1,
                page_views = caddy_ui.hourly_traffic_aggregates.page_views + EXCLUDED.page_views,
                bytes_sent = caddy_ui.hourly_traffic_aggregates.bytes_sent + EXCLUDED.bytes_sent,
                duration_sum_ms = caddy_ui.hourly_traffic_aggregates.duration_sum_ms + EXCLUDED.duration_sum_ms,
                duration_max_ms = GREATEST(
                    caddy_ui.hourly_traffic_aggregates.duration_max_ms,
                    EXCLUDED.duration_max_ms);

            INSERT INTO caddy_ui.daily_traffic_aggregates(
                bucket_start, host, status_class, actor_type, request_type,
                requests, page_views, bytes_sent, duration_sum_ms, duration_max_ms)
            VALUES(
                @day, @host, @status_class, @actor_type, @request_type,
                1, @page_views, @bytes_sent, @duration_ms, @duration_ms)
            ON CONFLICT (bucket_start, host, status_class, actor_type, request_type)
            DO UPDATE SET
                requests = caddy_ui.daily_traffic_aggregates.requests + 1,
                page_views = caddy_ui.daily_traffic_aggregates.page_views + EXCLUDED.page_views,
                bytes_sent = caddy_ui.daily_traffic_aggregates.bytes_sent + EXCLUDED.bytes_sent,
                duration_sum_ms = caddy_ui.daily_traffic_aggregates.duration_sum_ms + EXCLUDED.duration_sum_ms,
                duration_max_ms = GREATEST(
                    caddy_ui.daily_traffic_aggregates.duration_max_ms,
                    EXCLUDED.duration_max_ms);

            INSERT INTO caddy_ui.monthly_traffic_aggregates(
                bucket_start, host, status_class, actor_type, request_type,
                requests, page_views, bytes_sent, duration_sum_ms, duration_max_ms)
            VALUES(
                @month, @host, @status_class, @actor_type, @request_type,
                1, @page_views, @bytes_sent, @duration_ms, @duration_ms)
            ON CONFLICT (bucket_start, host, status_class, actor_type, request_type)
            DO UPDATE SET
                requests = caddy_ui.monthly_traffic_aggregates.requests + 1,
                page_views = caddy_ui.monthly_traffic_aggregates.page_views + EXCLUDED.page_views,
                bytes_sent = caddy_ui.monthly_traffic_aggregates.bytes_sent + EXCLUDED.bytes_sent,
                duration_sum_ms = caddy_ui.monthly_traffic_aggregates.duration_sum_ms + EXCLUDED.duration_sum_ms,
                duration_max_ms = GREATEST(
                    caddy_ui.monthly_traffic_aggregates.duration_max_ms,
                    EXCLUDED.duration_max_ms);

            INSERT INTO caddy_ui.route_performance_aggregates(
                bucket_start, granularity, host, path_pattern, request_type,
                request_count, error_count, duration_sum_ms, duration_max_ms,
                p50_ms, p95_ms, p99_ms)
            VALUES(
                @hour, 'hour', @host, @path_pattern, @request_type,
                1, @error_count, @duration_ms, @duration_ms, NULL, NULL, NULL)
            ON CONFLICT (bucket_start, granularity, host, path_pattern, request_type)
            DO UPDATE SET
                request_count = caddy_ui.route_performance_aggregates.request_count + 1,
                error_count = caddy_ui.route_performance_aggregates.error_count + EXCLUDED.error_count,
                duration_sum_ms = caddy_ui.route_performance_aggregates.duration_sum_ms + EXCLUDED.duration_sum_ms,
                duration_max_ms = GREATEST(
                    caddy_ui.route_performance_aggregates.duration_max_ms,
                    EXCLUDED.duration_max_ms);
            """,
            command =>
            {
                AddParameter(command, "hour", hour);
                AddParameter(command, "day", day);
                AddParameter(command, "month", month);
                AddParameter(command, "host", request.Host);
                AddParameter(command, "status_class", statusClass);
                AddParameter(command, "actor_type", classified.ActorType.ToStorageValue());
                AddParameter(command, "request_type", classified.RequestType.ToStorageValue());
                AddParameter(command, "page_views", pageViews);
                AddParameter(command, "bytes_sent", request.BytesSent);
                AddParameter(command, "duration_ms", request.DurationMilliseconds);
                AddParameter(command, "path_pattern", pathPattern);
                AddParameter(command, "error_count", request.Status >= 400 ? 1 : 0);
            },
            cancellationToken);
    }

    private static Task InsertFailureAsync(
        DbConnection connection,
        DbTransaction transaction,
        string source,
        AnalyticsIngestionFailure failure,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.ingestion_failures(
                source, source_offset, occurred_at, raw_line, error, resolved_at)
            VALUES(
                @source, @source_offset, @occurred_at, @raw_line, @error, NULL)
            """,
            command =>
            {
                AddParameter(command, "source", source);
                AddParameter(command, "source_offset", failure.SourceOffset);
                AddParameter(command, "occurred_at", DateTimeOffset.UtcNow);
                AddParameter(command, "raw_line", failure.SafeRawLine);
                AddParameter(command, "error", failure.Error);
            },
            cancellationToken);
    }

    private static Task UpsertCheckpointAsync(
        DbConnection connection,
        DbTransaction transaction,
        string source,
        string sourceIdentity,
        long byteOffset,
        DateTimeOffset? lastEventAt,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.analytics_checkpoints(
                source, source_identity, byte_offset, last_event_at,
                updated_at, metadata_json)
            VALUES(
                @source, @source_identity, @byte_offset, @last_event_at,
                @updated_at, '{}'::jsonb)
            ON CONFLICT (source)
            DO UPDATE SET
                source_identity = EXCLUDED.source_identity,
                byte_offset = EXCLUDED.byte_offset,
                last_event_at = COALESCE(
                    EXCLUDED.last_event_at,
                    caddy_ui.analytics_checkpoints.last_event_at),
                updated_at = EXCLUDED.updated_at
            """,
            command =>
            {
                AddParameter(command, "source", source);
                AddParameter(command, "source_identity", sourceIdentity);
                AddParameter(command, "byte_offset", byteOffset);
                AddParameter(command, "last_event_at", lastEventAt);
                AddParameter(command, "updated_at", DateTimeOffset.UtcNow);
            },
            cancellationToken);
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
            DateTime timestamp => new DateTimeOffset(
                DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
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
