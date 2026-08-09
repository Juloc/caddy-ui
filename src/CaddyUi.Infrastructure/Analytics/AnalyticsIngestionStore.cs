using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
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
    private readonly ConcurrentDictionary<DateOnly, byte> _knownPartitions = new();

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

        var preparedRequests = requests
            .OrderBy(item => item.Request.OccurredAt)
            .Select(item => new PreparedAnalyticsRequest(
                item,
                item.ActorType == AnalyticsActorType.Internal
                    ? null
                    : AnalyticsClientFingerprint.Create(item.Request, clientHashKey)))
            .ToArray();
        var lastEventAt = preparedRequests.Length == 0
            ? (DateTimeOffset?)null
            : preparedRequests[^1].Classified.Request.OccurredAt;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var ensuredMonths = preparedRequests
            .Select(item => new DateOnly(
                item.Classified.Request.OccurredAt.Year,
                item.Classified.Request.OccurredAt.Month,
                1))
            .Distinct()
            .Where(month => !_knownPartitions.ContainsKey(month))
            .ToArray();

        try
        {
            foreach (var month in ensuredMonths)
            {
                await EnsurePartitionAsync(connection, transaction, month, cancellationToken);
            }

            var clientIds = await GetOrCreateClientsAsync(
                connection,
                transaction,
                preparedRequests,
                cancellationToken);
            var insertedRequestIds = await InsertRequestsAsync(
                connection,
                transaction,
                preparedRequests,
                clientIds,
                cancellationToken);
            var insertedRequests = preparedRequests
                .Where(item => insertedRequestIds.Contains(item.Classified.Request.Id))
                .ToArray();

            var pageViewsInserted = 0;
            var sessionCache = new Dictionary<SessionCacheKey, SessionCacheEntry>();
            var sessionDeltas = new Dictionary<Guid, SessionDelta>();
            var pageLoadRequests = new List<PageLoadRequest>(insertedRequests.Length);
            var idleTimeout = TimeSpan.FromMinutes(options.SessionIdleMinutes);

            foreach (var prepared in insertedRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var classified = prepared.Classified;
                var request = classified.Request;
                var clientId = GetClientId(prepared, clientIds);
                Guid? sessionId = clientId is null ||
                    classified.ActorType is AnalyticsActorType.Bot or AnalyticsActorType.Internal
                        ? null
                        : await ResolveSessionAsync(
                            connection,
                            transaction,
                            sessionCache,
                            clientId.Value,
                            request.Host,
                            request.OccurredAt,
                            idleTimeout,
                            classified.IsNavigation,
                            cancellationToken);

                if (sessionId is not null)
                {
                    AddSessionDelta(
                        sessionDeltas,
                        sessionId.Value,
                        request.OccurredAt,
                        classified.IsPageView);
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
                    pageLoadRequests.Add(
                        new PageLoadRequest(
                            clientId.Value,
                            classified));
                }
            }

            await UpdateRecentPageLoadsAsync(
                connection,
                transaction,
                pageLoadRequests,
                options.PageLoadWindowSeconds,
                cancellationToken);
            await UpdateSessionsAsync(
                connection,
                transaction,
                sessionDeltas,
                cancellationToken);
            await UpsertAggregatesAsync(
                connection,
                transaction,
                insertedRequests.Select(item => item.Classified).ToArray(),
                cancellationToken);

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

            foreach (var month in ensuredMonths)
            {
                _knownPartitions.TryAdd(month, 0);
            }

            return new AnalyticsPersistResult(
                insertedRequestIds.Count,
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

        var currentMonth = new DateOnly(now.Year, now.Month, 1);
        var next = now.AddMonths(1);
        var nextMonth = new DateOnly(next.Year, next.Month, 1);

        try
        {
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

                DELETE FROM caddy_ui.request_events_default
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

            await EnsurePartitionAsync(
                connection,
                transaction,
                currentMonth,
                cancellationToken);
            await EnsurePartitionAsync(
                connection,
                transaction,
                nextMonth,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _knownPartitions.TryAdd(currentMonth, 0);
            _knownPartitions.TryAdd(nextMonth, 0);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<Dictionary<string, Guid>> GetOrCreateClientsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<PreparedAnalyticsRequest> requests,
        CancellationToken cancellationToken)
    {
        var candidates = requests
            .Where(item => item.Identity is not null)
            .GroupBy(item => item.Identity!.ClientKey, StringComparer.Ordinal)
            .Select(group => group.MaxBy(item => item.Classified.Request.OccurredAt)!)
            .ToArray();
        if (candidates.Length == 0)
        {
            return new Dictionary<string, Guid>(StringComparer.Ordinal);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sql = new StringBuilder(
            """
            INSERT INTO caddy_ui.anonymous_clients(
                id, client_key, first_seen_at, last_seen_at,
                first_party_identifier_hash, metadata_json)
            VALUES
            """);

        for (var index = 0; index < candidates.Length; index++)
        {
            if (index > 0)
            {
                sql.Append(',');
            }

            sql.Append(
                CultureInfo.InvariantCulture,
                $"""

                (@id_{index}, @client_key_{index}, @occurred_at_{index}, @occurred_at_{index},
                 @first_party_identifier_hash_{index}, CAST(@metadata_json_{index} AS jsonb))
                """);

            var candidate = candidates[index];
            var identity = candidate.Identity!;
            AddParameter(command, $"id_{index}", Guid.NewGuid());
            AddParameter(command, $"client_key_{index}", identity.ClientKey);
            AddParameter(
                command,
                $"occurred_at_{index}",
                candidate.Classified.Request.OccurredAt);
            AddParameter(
                command,
                $"first_party_identifier_hash_{index}",
                identity.FirstPartyIdentifierHash);
            AddParameter(
                command,
                $"metadata_json_{index}",
                JsonSerializer.Serialize(new Dictionary<string, bool>
                {
                    ["estimated"] = identity.Estimated,
                }));
        }

        sql.Append(
            """

            ON CONFLICT (client_key)
            DO UPDATE SET
                last_seen_at = GREATEST(
                    caddy_ui.anonymous_clients.last_seen_at,
                    EXCLUDED.last_seen_at),
                first_party_identifier_hash = COALESCE(
                    caddy_ui.anonymous_clients.first_party_identifier_hash,
                    EXCLUDED.first_party_identifier_hash),
                metadata_json = caddy_ui.anonymous_clients.metadata_json || EXCLUDED.metadata_json
            RETURNING client_key, id
            """);
        command.CommandText = sql.ToString();

        var clientIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            clientIds[reader.GetString(0)] = reader.GetGuid(1);
        }

        return clientIds;
    }

    private static async Task<HashSet<Guid>> InsertRequestsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<PreparedAnalyticsRequest> requests,
        IReadOnlyDictionary<string, Guid> clientIds,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sql = new StringBuilder(
            """
            INSERT INTO caddy_ui.request_events(
                id, occurred_at, source_file, source_offset, host, method, path,
                query_string, status, duration_ms, bytes_sent, remote_address,
                user_agent, referer, accept_header, content_type, sec_fetch_dest,
                actor_type, request_type, classification_confidence,
                managed_route_id, anonymous_client_id, raw_json)
            VALUES
            """);

        for (var index = 0; index < requests.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(',');
            }

            sql.Append(
                CultureInfo.InvariantCulture,
                $"""

                (@id_{index}, @occurred_at_{index}, @source_file_{index}, @source_offset_{index},
                 @host_{index}, @method_{index}, @path_{index}, @query_string_{index},
                 @status_{index}, @duration_ms_{index}, @bytes_sent_{index},
                 CAST(@remote_address_{index} AS inet), @user_agent_{index}, @referer_{index},
                 @accept_header_{index}, @content_type_{index}, @sec_fetch_dest_{index},
                 @actor_type_{index}, @request_type_{index}, @classification_confidence_{index},
                 NULL, @anonymous_client_id_{index}, CAST(@raw_json_{index} AS jsonb))
                """);

            var prepared = requests[index];
            var classified = prepared.Classified;
            var request = classified.Request;
            AddParameter(command, $"id_{index}", request.Id);
            AddParameter(command, $"occurred_at_{index}", request.OccurredAt);
            AddParameter(command, $"source_file_{index}", request.SourceFile);
            AddParameter(command, $"source_offset_{index}", request.SourceOffset);
            AddParameter(command, $"host_{index}", request.Host);
            AddParameter(command, $"method_{index}", request.Method);
            AddParameter(command, $"path_{index}", request.Path);
            AddParameter(command, $"query_string_{index}", request.QueryString);
            AddParameter(command, $"status_{index}", request.Status);
            AddParameter(command, $"duration_ms_{index}", request.DurationMilliseconds);
            AddParameter(command, $"bytes_sent_{index}", request.BytesSent);
            AddParameter(command, $"remote_address_{index}", request.RemoteAddress);
            AddParameter(command, $"user_agent_{index}", request.UserAgent);
            AddParameter(command, $"referer_{index}", request.Referer);
            AddParameter(command, $"accept_header_{index}", request.AcceptHeader);
            AddParameter(command, $"content_type_{index}", request.ContentType);
            AddParameter(command, $"sec_fetch_dest_{index}", request.SecFetchDest);
            AddParameter(command, $"actor_type_{index}", classified.ActorType.ToStorageValue());
            AddParameter(command, $"request_type_{index}", classified.RequestType.ToStorageValue());
            AddParameter(
                command,
                $"classification_confidence_{index}",
                classified.Confidence.ToStorageValue());
            AddParameter(
                command,
                $"anonymous_client_id_{index}",
                GetClientId(prepared, clientIds));
            AddParameter(command, $"raw_json_{index}", request.RawJson);
        }

        sql.Append(
            """

            ON CONFLICT DO NOTHING
            RETURNING id
            """);
        command.CommandText = sql.ToString();

        var inserted = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            inserted.Add(reader.GetGuid(0));
        }

        return inserted;
    }

    private static Guid? GetClientId(
        PreparedAnalyticsRequest request,
        IReadOnlyDictionary<string, Guid> clientIds)
    {
        if (request.Identity is null)
        {
            return null;
        }

        return clientIds.TryGetValue(request.Identity.ClientKey, out var clientId)
            ? clientId
            : throw new InvalidOperationException(
                "The analytics client could not be resolved for the current batch.");
    }

    private static async Task<Guid?> ResolveSessionAsync(
        DbConnection connection,
        DbTransaction transaction,
        IDictionary<SessionCacheKey, SessionCacheEntry> cache,
        Guid clientId,
        string host,
        DateTimeOffset occurredAt,
        TimeSpan idleTimeout,
        bool createWhenMissing,
        CancellationToken cancellationToken)
    {
        var key = new SessionCacheKey(clientId, host);
        if (cache.TryGetValue(key, out var cached) &&
            occurredAt >= cached.LastActivityAt &&
            occurredAt - cached.LastActivityAt <= idleTimeout)
        {
            if (cached.SessionId is not null || !createWhenMissing)
            {
                cache[key] = cached with { LastActivityAt = occurredAt };
                return cached.SessionId;
            }
        }

        var sessionId = await FindOrCreateSessionAsync(
            connection,
            transaction,
            clientId,
            host,
            occurredAt,
            idleTimeout,
            createWhenMissing,
            cancellationToken);
        cache[key] = new SessionCacheEntry(sessionId, occurredAt);
        return sessionId;
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

    private static void AddSessionDelta(
        IDictionary<Guid, SessionDelta> deltas,
        Guid sessionId,
        DateTimeOffset occurredAt,
        bool isPageView)
    {
        if (deltas.TryGetValue(sessionId, out var existing))
        {
            deltas[sessionId] = existing with
            {
                LastActivityAt = occurredAt > existing.LastActivityAt
                    ? occurredAt
                    : existing.LastActivityAt,
                RequestCount = existing.RequestCount + 1,
                PageViewCount = existing.PageViewCount + (isPageView ? 1 : 0),
            };
            return;
        }

        deltas[sessionId] = new SessionDelta(
            occurredAt,
            1,
            isPageView ? 1 : 0);
    }

    private static Task UpdateSessionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<Guid, SessionDelta> deltas,
        CancellationToken cancellationToken)
    {
        if (deltas.Count == 0)
        {
            return Task.CompletedTask;
        }

        return ExecuteDynamicAsync(
            connection,
            transaction,
            command =>
            {
                var sql = new StringBuilder(
                    """
                    WITH updates(id, last_activity_at, request_count, page_view_count) AS (
                        VALUES
                    """);
                var index = 0;
                foreach (var (sessionId, delta) in deltas)
                {
                    if (index > 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append(
                        CultureInfo.InvariantCulture,
                        $"""

                        (@id_{index}, @last_activity_at_{index}, @request_count_{index}, @page_view_count_{index})
                        """);
                    AddParameter(command, $"id_{index}", sessionId);
                    AddParameter(command, $"last_activity_at_{index}", delta.LastActivityAt);
                    AddParameter(command, $"request_count_{index}", delta.RequestCount);
                    AddParameter(command, $"page_view_count_{index}", delta.PageViewCount);
                    index++;
                }

                sql.Append(
                    """

                    )
                    UPDATE caddy_ui.analytics_sessions AS sessions
                    SET last_activity_at = GREATEST(
                            sessions.last_activity_at,
                            updates.last_activity_at),
                        request_count = sessions.request_count + updates.request_count,
                        page_view_count = sessions.page_view_count + updates.page_view_count
                    FROM updates
                    WHERE sessions.id = updates.id
                    """);
                return sql.ToString();
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

    private static Task UpdateRecentPageLoadsAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<PageLoadRequest> requests,
        int pageLoadWindowSeconds,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return Task.CompletedTask;
        }

        return ExecuteDynamicAsync(
            connection,
            transaction,
            command =>
            {
                var sql = new StringBuilder(
                    """
                    WITH events(
                        client_id, host, occurred_at,
                        asset_increment, api_increment, bytes_sent) AS (
                        VALUES
                    """);

                for (var index = 0; index < requests.Count; index++)
                {
                    if (index > 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append(
                        CultureInfo.InvariantCulture,
                        $"""

                        (@client_id_{index}, @host_{index}, @occurred_at_{index},
                         @asset_increment_{index}, @api_increment_{index}, @bytes_sent_{index})
                        """);

                    var request = requests[index];
                    AddParameter(command, $"client_id_{index}", request.ClientId);
                    AddParameter(command, $"host_{index}", request.Classified.Request.Host);
                    AddParameter(
                        command,
                        $"occurred_at_{index}",
                        request.Classified.Request.OccurredAt);
                    AddParameter(
                        command,
                        $"asset_increment_{index}",
                        request.Classified.RequestType == AnalyticsRequestType.Asset ? 1 : 0);
                    AddParameter(
                        command,
                        $"api_increment_{index}",
                        request.Classified.RequestType == AnalyticsRequestType.Api ? 1 : 0);
                    AddParameter(
                        command,
                        $"bytes_sent_{index}",
                        request.Classified.Request.BytesSent);
                }

                AddParameter(command, "window_seconds", pageLoadWindowSeconds);
                sql.Append(
                    """

                    ),
                    mapped AS (
                        SELECT recent.id,
                               COUNT(*)::integer AS request_increment,
                               COALESCE(SUM(events.asset_increment), 0)::integer AS asset_increment,
                               COALESCE(SUM(events.api_increment), 0)::integer AS api_increment,
                               COALESCE(SUM(events.bytes_sent), 0)::bigint AS bytes_increment,
                               MAX(events.occurred_at) AS completed_at
                        FROM events
                        JOIN LATERAL (
                            SELECT loads.id
                            FROM caddy_ui.page_loads AS loads
                            JOIN caddy_ui.page_views AS views
                              ON views.id = loads.page_view_id
                            WHERE views.anonymous_client_id = events.client_id
                              AND views.host = events.host
                              AND loads.started_at <= events.occurred_at
                              AND loads.started_at >=
                                  events.occurred_at - (@window_seconds * INTERVAL '1 second')
                            ORDER BY loads.started_at DESC
                            LIMIT 1
                        ) AS recent ON TRUE
                        GROUP BY recent.id
                    )
                    UPDATE caddy_ui.page_loads AS loads
                    SET request_count = loads.request_count + mapped.request_increment,
                        asset_request_count =
                            loads.asset_request_count + mapped.asset_increment,
                        api_request_count =
                            loads.api_request_count + mapped.api_increment,
                        bytes_sent = loads.bytes_sent + mapped.bytes_increment,
                        completed_at = GREATEST(
                            COALESCE(loads.completed_at, mapped.completed_at),
                            mapped.completed_at)
                    FROM mapped
                    WHERE loads.id = mapped.id
                    """);
                return sql.ToString();
            },
            cancellationToken);
    }

    private static async Task UpsertAggregatesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<ClassifiedRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return;
        }

        await UpsertTrafficAggregatesAsync(
            connection,
            transaction,
            "hourly_traffic_aggregates",
            BuildTrafficAggregateRows(
                requests,
                item => GetHourBucket(item.Request.OccurredAt)),
            cancellationToken);
        await UpsertTrafficAggregatesAsync(
            connection,
            transaction,
            "daily_traffic_aggregates",
            BuildTrafficAggregateRows(
                requests,
                item => GetDayBucket(item.Request.OccurredAt)),
            cancellationToken);
        await UpsertTrafficAggregatesAsync(
            connection,
            transaction,
            "monthly_traffic_aggregates",
            BuildTrafficAggregateRows(
                requests,
                item => GetMonthBucket(item.Request.OccurredAt)),
            cancellationToken);
        await UpsertRouteAggregatesAsync(
            connection,
            transaction,
            BuildRouteAggregateRows(requests),
            cancellationToken);
    }

    private static IReadOnlyList<TrafficAggregateRow> BuildTrafficAggregateRows(
        IReadOnlyList<ClassifiedRequest> requests,
        Func<ClassifiedRequest, object> bucketSelector)
    {
        return requests
            .GroupBy(item => new TrafficAggregateKey(
                bucketSelector(item),
                item.Request.Host,
                GetStatusClass(item.Request.Status),
                item.ActorType.ToStorageValue(),
                item.RequestType.ToStorageValue()))
            .Select(group => new TrafficAggregateRow(
                group.Key.BucketStart,
                group.Key.Host,
                group.Key.StatusClass,
                group.Key.ActorType,
                group.Key.RequestType,
                group.LongCount(),
                group.LongCount(item => item.IsPageView),
                group.Sum(item => item.Request.BytesSent),
                group.Sum(item => item.Request.DurationMilliseconds),
                group.Max(item => item.Request.DurationMilliseconds)))
            .ToArray();
    }

    private static Task UpsertTrafficAggregatesAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        IReadOnlyList<TrafficAggregateRow> rows,
        CancellationToken cancellationToken)
    {
        return ExecuteDynamicAsync(
            connection,
            transaction,
            command =>
            {
                var sql = new StringBuilder();
                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"""
                    INSERT INTO caddy_ui.{table}(
                        bucket_start, host, status_class, actor_type, request_type,
                        requests, page_views, bytes_sent, duration_sum_ms, duration_max_ms)
                    VALUES
                    """);

                for (var index = 0; index < rows.Count; index++)
                {
                    if (index > 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append(
                        CultureInfo.InvariantCulture,
                        $"""

                        (@bucket_start_{index}, @host_{index}, @status_class_{index},
                         @actor_type_{index}, @request_type_{index}, @requests_{index},
                         @page_views_{index}, @bytes_sent_{index}, @duration_sum_ms_{index},
                         @duration_max_ms_{index})
                        """);
                    var row = rows[index];
                    AddParameter(command, $"bucket_start_{index}", row.BucketStart);
                    AddParameter(command, $"host_{index}", row.Host);
                    AddParameter(command, $"status_class_{index}", row.StatusClass);
                    AddParameter(command, $"actor_type_{index}", row.ActorType);
                    AddParameter(command, $"request_type_{index}", row.RequestType);
                    AddParameter(command, $"requests_{index}", row.Requests);
                    AddParameter(command, $"page_views_{index}", row.PageViews);
                    AddParameter(command, $"bytes_sent_{index}", row.BytesSent);
                    AddParameter(command, $"duration_sum_ms_{index}", row.DurationSumMilliseconds);
                    AddParameter(command, $"duration_max_ms_{index}", row.DurationMaxMilliseconds);
                }

                sql.Append(
                    CultureInfo.InvariantCulture,
                    $"""

                    ON CONFLICT (bucket_start, host, status_class, actor_type, request_type)
                    DO UPDATE SET
                        requests = caddy_ui.{table}.requests + EXCLUDED.requests,
                        page_views = caddy_ui.{table}.page_views + EXCLUDED.page_views,
                        bytes_sent = caddy_ui.{table}.bytes_sent + EXCLUDED.bytes_sent,
                        duration_sum_ms =
                            caddy_ui.{table}.duration_sum_ms + EXCLUDED.duration_sum_ms,
                        duration_max_ms = GREATEST(
                            caddy_ui.{table}.duration_max_ms,
                            EXCLUDED.duration_max_ms)
                    """);
                return sql.ToString();
            },
            cancellationToken);
    }

    private static IReadOnlyList<RouteAggregateRow> BuildRouteAggregateRows(
        IReadOnlyList<ClassifiedRequest> requests)
    {
        return requests
            .GroupBy(item => new RouteAggregateKey(
                GetHourBucket(item.Request.OccurredAt),
                item.Request.Host,
                PathCardinalityNormalizer.Normalize(item.Request.Path),
                item.RequestType.ToStorageValue()))
            .Select(group => new RouteAggregateRow(
                group.Key.BucketStart,
                group.Key.Host,
                group.Key.PathPattern,
                group.Key.RequestType,
                group.LongCount(),
                group.LongCount(item => item.Request.Status >= 400),
                group.Sum(item => item.Request.DurationMilliseconds),
                group.Max(item => item.Request.DurationMilliseconds)))
            .ToArray();
    }

    private static Task UpsertRouteAggregatesAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<RouteAggregateRow> rows,
        CancellationToken cancellationToken)
    {
        return ExecuteDynamicAsync(
            connection,
            transaction,
            command =>
            {
                var sql = new StringBuilder(
                    """
                    INSERT INTO caddy_ui.route_performance_aggregates(
                        bucket_start, granularity, host, path_pattern, request_type,
                        request_count, error_count, duration_sum_ms, duration_max_ms,
                        p50_ms, p95_ms, p99_ms)
                    VALUES
                    """);

                for (var index = 0; index < rows.Count; index++)
                {
                    if (index > 0)
                    {
                        sql.Append(',');
                    }

                    sql.Append(
                        CultureInfo.InvariantCulture,
                        $"""

                        (@bucket_start_{index}, 'hour', @host_{index}, @path_pattern_{index},
                         @request_type_{index}, @request_count_{index}, @error_count_{index},
                         @duration_sum_ms_{index}, @duration_max_ms_{index}, NULL, NULL, NULL)
                        """);
                    var row = rows[index];
                    AddParameter(command, $"bucket_start_{index}", row.BucketStart);
                    AddParameter(command, $"host_{index}", row.Host);
                    AddParameter(command, $"path_pattern_{index}", row.PathPattern);
                    AddParameter(command, $"request_type_{index}", row.RequestType);
                    AddParameter(command, $"request_count_{index}", row.RequestCount);
                    AddParameter(command, $"error_count_{index}", row.ErrorCount);
                    AddParameter(command, $"duration_sum_ms_{index}", row.DurationSumMilliseconds);
                    AddParameter(command, $"duration_max_ms_{index}", row.DurationMaxMilliseconds);
                }

                sql.Append(
                    """

                    ON CONFLICT (bucket_start, granularity, host, path_pattern, request_type)
                    DO UPDATE SET
                        request_count =
                            caddy_ui.route_performance_aggregates.request_count +
                            EXCLUDED.request_count,
                        error_count =
                            caddy_ui.route_performance_aggregates.error_count +
                            EXCLUDED.error_count,
                        duration_sum_ms =
                            caddy_ui.route_performance_aggregates.duration_sum_ms +
                            EXCLUDED.duration_sum_ms,
                        duration_max_ms = GREATEST(
                            caddy_ui.route_performance_aggregates.duration_max_ms,
                            EXCLUDED.duration_max_ms)
                    """);
                return sql.ToString();
            },
            cancellationToken);
    }

    private static DateTimeOffset GetHourBucket(DateTimeOffset occurredAt)
    {
        var utc = occurredAt.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            0,
            0,
            TimeSpan.Zero);
    }

    private static DateOnly GetDayBucket(DateTimeOffset occurredAt)
    {
        var utc = occurredAt.ToUniversalTime();
        return new DateOnly(utc.Year, utc.Month, utc.Day);
    }

    private static DateOnly GetMonthBucket(DateTimeOffset occurredAt)
    {
        var utc = occurredAt.ToUniversalTime();
        return new DateOnly(utc.Year, utc.Month, 1);
    }

    private static string GetStatusClass(int status)
    {
        return status is >= 100 and <= 599
            ? string.Create(CultureInfo.InvariantCulture, $"{status / 100}xx")
            : "other";
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

    private static async Task ExecuteDynamicAsync(
        DbConnection connection,
        DbTransaction transaction,
        Func<DbCommand, string> buildSql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = buildSql(command);
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

    private sealed record PreparedAnalyticsRequest(
        ClassifiedRequest Classified,
        AnalyticsClientIdentity? Identity);

    private readonly record struct SessionCacheKey(Guid ClientId, string Host);

    private sealed record SessionCacheEntry(
        Guid? SessionId,
        DateTimeOffset LastActivityAt);

    private sealed record SessionDelta(
        DateTimeOffset LastActivityAt,
        long RequestCount,
        int PageViewCount);

    private sealed record PageLoadRequest(
        Guid ClientId,
        ClassifiedRequest Classified);

    private sealed record TrafficAggregateKey(
        object BucketStart,
        string Host,
        string StatusClass,
        string ActorType,
        string RequestType);

    private sealed record TrafficAggregateRow(
        object BucketStart,
        string Host,
        string StatusClass,
        string ActorType,
        string RequestType,
        long Requests,
        long PageViews,
        long BytesSent,
        double DurationSumMilliseconds,
        double DurationMaxMilliseconds);

    private sealed record RouteAggregateKey(
        DateTimeOffset BucketStart,
        string Host,
        string PathPattern,
        string RequestType);

    private sealed record RouteAggregateRow(
        DateTimeOffset BucketStart,
        string Host,
        string PathPattern,
        string RequestType,
        long RequestCount,
        long ErrorCount,
        double DurationSumMilliseconds,
        double DurationMaxMilliseconds);
}
