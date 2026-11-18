using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaddyUi.Application.Analytics;
using CaddyUi.Domain.Analytics;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Analytics;

public sealed record AnalyticsIngestionFailure(long? SourceOffset, string SafeRawLine, string Error);
public sealed record AnalyticsPersistResult(int RequestsProcessed, int TrafficBucketsWritten, int FailuresInserted);
public sealed record AnalyticsRetention(int StatisticsDays, int SecurityEventDays);

public sealed class AnalyticsIngestionStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory = contextFactory;

    public async Task<AnalyticsPersistResult> PersistBatchAsync(string source, string sourceIdentity, long byteOffset,
        IReadOnlyList<ClassifiedRequest> requests, IReadOnlyList<AnalyticsIngestionFailure> failures, byte[] clientHashKey,
        AnalyticsIngestionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(options);
        if (clientHashKey is null || clientHashKey.Length == 0)
            throw new ArgumentException("The client hash key is required.", nameof(clientHashKey));
        if (requests.Count + failures.Count > 5000)
            throw new ArgumentOutOfRangeException(nameof(requests));
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long previousOffset = -1;
        await using (var checkpointCommand = connection.CreateCommand())
        {
            checkpointCommand.Transaction = transaction;
            checkpointCommand.CommandText = """
                SELECT pg_advisory_xact_lock(hashtextextended(@source, 0));
                SELECT source_identity, byte_offset FROM caddy_ui.analytics_checkpoints WHERE source = @source;
                """;
            AddParameter(checkpointCommand, "source", source);
            await using var reader = await checkpointCommand.ExecuteReaderAsync(cancellationToken);
            await reader.NextResultAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && reader.GetString(0) == sourceIdentity)
                previousOffset = reader.GetInt64(1);
        }
        if (previousOffset >= byteOffset)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AnalyticsPersistResult(0, 0, 0);
        }
        var prepared = requests.Where(item => item.Request.SourceOffset >= previousOffset)
            .OrderBy(item => item.Request.OccurredAt)
            .Select(item => new PreparedAnalyticsRequest(item, item.ActorType == AnalyticsActorType.Internal ? null : AnalyticsClientFingerprint.Create(item.Request, clientHashKey))).ToArray();
        var clients = await GetOrCreateClientsAsync(connection, transaction, prepared, cancellationToken);
        var traffic = prepared.GroupBy(item => new
        {
            bucket_start = Bucket(item.Classified.Request.OccurredAt, 5),
            host = item.Classified.Request.Host,
            actor_type = item.Classified.ActorType.ToStorageValue(),
            status_class = item.Classified.Request.Status / 100
        }).Select(group => new
        {
            group.Key.bucket_start,
            group.Key.host,
            group.Key.actor_type,
            group.Key.status_class,
            requests = group.LongCount(),
            bytes_sent = group.Sum(item => item.Classified.Request.BytesSent)
        }).ToArray();
        if (traffic.Length > 0)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO caddy_ui.traffic_buckets(bucket_start, host, actor_type, status_class, requests, bytes_sent)
                SELECT bucket_start, host, actor_type, status_class, requests, bytes_sent
                FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS batch(
                    bucket_start timestamptz, host text, actor_type text, status_class integer, requests bigint, bytes_sent bigint)
                ON CONFLICT (bucket_start, host, actor_type, status_class) DO UPDATE
                SET requests = caddy_ui.traffic_buckets.requests + EXCLUDED.requests,
                    bytes_sent = caddy_ui.traffic_buckets.bytes_sent + EXCLUDED.bytes_sent
                """, command => AddParameter(command, "rows", JsonSerializer.Serialize(traffic)), cancellationToken);
        }
        var activity = prepared.Where(item => item.Identity is not null)
            .GroupBy(item => new { client_id = clients[item.Identity!.ClientKey], bucket_start = Bucket(item.Classified.Request.OccurredAt, 1) })
            .Select(group =>
            {
                var items = group.Select(item => item.Classified).ToArray();
                var gaps = items.Zip(items.Skip(1), (left, right) => (right.Request.OccurredAt - left.Request.OccurredAt).TotalSeconds).ToArray();
                var last = items[^1].Request;
                return new
                {
                    group.Key.client_id,
                    group.Key.bucket_start,
                    first_seen_at = items[0].Request.OccurredAt,
                    last_seen_at = last.OccurredAt,
                    remote_address = last.RemoteAddress,
                    user_agent = last.UserAgent[..Math.Min(512, last.UserAgent.Length)],
                    is_bot = items.Any(item => item.ActorType == AnalyticsActorType.Bot),
                    is_human = items.Any(item => item.ActorType == AnalyticsActorType.Human),
                    request_count = items.LongLength,
                    not_found_count = items.LongCount(item => item.Request.Status == 404),
                    auth_failure_count = items.LongCount(item => item.Request.Status is 401 or 403),
                    unsafe_method_count = items.LongCount(item => item.Request.Method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS")),
                    scanner_count = items.LongCount(item => ScannerPaths.Any(fragment => item.Request.Path.Contains(fragment, StringComparison.OrdinalIgnoreCase))),
                    gap_count = gaps.LongLength,
                    gap_sum = gaps.Sum(),
                    gap_squares = gaps.Sum(gap => gap * gap),
                    path_hashes = items.Select(item => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(item.Request.Path)))).Distinct().Take(64).ToArray(),
                    hosts = items.Select(item => item.Request.Host).Distinct().Take(8).ToArray()
                };
            }).ToArray();
        if (activity.Length > 0)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO caddy_ui.client_activity_buckets AS activity(
                    client_id, bucket_start, first_seen_at, last_seen_at, remote_address, user_agent, is_bot, is_human,
                    request_count, not_found_count, auth_failure_count, unsafe_method_count, scanner_count,
                    gap_count, gap_sum, gap_squares, path_hashes, hosts)
                SELECT client_id, bucket_start, first_seen_at, last_seen_at, CAST(remote_address AS inet), user_agent, is_bot, is_human,
                       request_count, not_found_count, auth_failure_count, unsafe_method_count, scanner_count,
                       gap_count, gap_sum, gap_squares, path_hashes, hosts
                FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS batch(
                    client_id uuid, bucket_start timestamptz, first_seen_at timestamptz, last_seen_at timestamptz,
                    remote_address text, user_agent text, is_bot boolean, is_human boolean,
                    request_count bigint, not_found_count bigint, auth_failure_count bigint, unsafe_method_count bigint, scanner_count bigint,
                    gap_count bigint, gap_sum double precision, gap_squares double precision, path_hashes jsonb, hosts jsonb)
                ON CONFLICT (client_id, bucket_start) DO UPDATE SET
                    first_seen_at = LEAST(activity.first_seen_at, EXCLUDED.first_seen_at),
                    last_seen_at = GREATEST(activity.last_seen_at, EXCLUDED.last_seen_at),
                    remote_address = CASE WHEN EXCLUDED.last_seen_at >= activity.last_seen_at THEN EXCLUDED.remote_address ELSE activity.remote_address END,
                    user_agent = CASE WHEN EXCLUDED.last_seen_at >= activity.last_seen_at THEN EXCLUDED.user_agent ELSE activity.user_agent END,
                    is_bot = activity.is_bot OR EXCLUDED.is_bot, is_human = activity.is_human OR EXCLUDED.is_human,
                    request_count = activity.request_count + EXCLUDED.request_count,
                    not_found_count = activity.not_found_count + EXCLUDED.not_found_count,
                    auth_failure_count = activity.auth_failure_count + EXCLUDED.auth_failure_count,
                    unsafe_method_count = activity.unsafe_method_count + EXCLUDED.unsafe_method_count,
                    scanner_count = activity.scanner_count + EXCLUDED.scanner_count,
                    gap_count = activity.gap_count + EXCLUDED.gap_count + CASE WHEN EXCLUDED.first_seen_at >= activity.last_seen_at THEN 1 ELSE 0 END,
                    gap_sum = activity.gap_sum + EXCLUDED.gap_sum + GREATEST(0, EXTRACT(EPOCH FROM EXCLUDED.first_seen_at - activity.last_seen_at)),
                    gap_squares = activity.gap_squares + EXCLUDED.gap_squares + POWER(GREATEST(0, EXTRACT(EPOCH FROM EXCLUDED.first_seen_at - activity.last_seen_at)), 2),
                    path_hashes = (SELECT jsonb_agg(value) FROM (SELECT DISTINCT value FROM jsonb_array_elements(activity.path_hashes || EXCLUDED.path_hashes) LIMIT 64) AS paths),
                    hosts = (SELECT jsonb_agg(value) FROM (SELECT DISTINCT value FROM jsonb_array_elements(activity.hosts || EXCLUDED.hosts) LIMIT 8) AS hosts)
                """, command => AddParameter(command, "rows", JsonSerializer.Serialize(activity)), cancellationToken);
        }
        var newFailures = failures.Where(item => item.SourceOffset is null || item.SourceOffset >= previousOffset).ToArray();
        if (newFailures.Length > 0)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO caddy_ui.ingestion_failures(source, source_offset, occurred_at, raw_line, error, resolved_at)
                SELECT @source, source_offset, CURRENT_TIMESTAMP, safe_line, error, NULL
                FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS batch(source_offset bigint, safe_line text, error text)
                """, command =>
                {
                    AddParameter(command, "source", source);
                    AddParameter(command, "rows", JsonSerializer.Serialize(newFailures.Select(item => new { source_offset = item.SourceOffset, safe_line = item.SafeRawLine, error = item.Error })));
                }, cancellationToken);
        }
        await UpsertCheckpointAsync(connection, transaction, source, sourceIdentity, byteOffset,
            prepared.Length == 0 ? null : prepared[^1].Classified.Request.OccurredAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AnalyticsPersistResult(prepared.Length, traffic.Length, newFailures.Length);
    }

    public async Task RunMaintenanceAsync(AnalyticsIngestionOptions options, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var retention = await ReadRetentionAsync(connection, transaction, options, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM caddy_ui.traffic_buckets WHERE bucket_start < @statistics_cutoff;
            DELETE FROM caddy_ui.client_activity_buckets WHERE last_seen_at < @evidence_cutoff;
            DELETE FROM caddy_ui.security_events
            WHERE occurred_at < @security_cutoff AND occurred_at >=
                (SELECT (value_json ->> 'at')::timestamptz FROM caddy_ui.application_settings WHERE key = 'analytics.lightweight_started_at');
            DELETE FROM caddy_ui.client_assessment_reasons AS reasons USING caddy_ui.client_assessments AS assessments
            WHERE reasons.assessment_id = assessments.id AND assessments.created_at < @security_cutoff AND assessments.created_at >=
                (SELECT (value_json ->> 'at')::timestamptz FROM caddy_ui.application_settings WHERE key = 'analytics.lightweight_started_at');
            DELETE FROM caddy_ui.client_assessments
            WHERE created_at < @security_cutoff AND created_at >=
                (SELECT (value_json ->> 'at')::timestamptz FROM caddy_ui.application_settings WHERE key = 'analytics.lightweight_started_at');
            DELETE FROM caddy_ui.ingestion_failures
            WHERE occurred_at < @security_cutoff AND occurred_at >=
                (SELECT (value_json ->> 'at')::timestamptz FROM caddy_ui.application_settings WHERE key = 'analytics.lightweight_started_at');
            WITH expired AS (
                SELECT clients.id FROM caddy_ui.anonymous_clients AS clients
                WHERE clients.last_seen_at < @security_cutoff AND clients.first_seen_at >=
                    (SELECT (value_json ->> 'at')::timestamptz FROM caddy_ui.application_settings WHERE key = 'analytics.lightweight_started_at')
                  AND NOT EXISTS (SELECT 1 FROM caddy_ui.client_activity_buckets WHERE client_id = clients.id)
                  AND NOT EXISTS (SELECT 1 FROM caddy_ui.client_assessments WHERE anonymous_client_id = clients.id)
                  AND NOT EXISTS (SELECT 1 FROM caddy_ui.security_events WHERE anonymous_client_id = clients.id)
                  AND NOT EXISTS (SELECT 1 FROM caddy_ui.request_events WHERE anonymous_client_id = clients.id)
                  AND NOT EXISTS (SELECT 1 FROM caddy_ui.page_views WHERE anonymous_client_id = clients.id)
                  AND NOT EXISTS (SELECT 1 FROM caddy_ui.analytics_sessions WHERE anonymous_client_id = clients.id)
                ORDER BY clients.last_seen_at LIMIT 1000
            )
            DELETE FROM caddy_ui.anonymous_clients WHERE id IN (SELECT id FROM expired);
            """, command =>
            {
                AddParameter(command, "statistics_cutoff", now.AddDays(-retention.StatisticsDays));
                AddParameter(command, "security_cutoff", now.AddDays(-retention.SecurityEventDays));
                AddParameter(command, "evidence_cutoff", now.AddMinutes(-options.SecurityEvidenceMinutes));
            }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveRetentionAsync(int statisticsDays, int securityEventDays, CancellationToken cancellationToken = default)
    {
        if (statisticsDays is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(statisticsDays));
        if (securityEventDays is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(securityEventDays));
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO caddy_ui.application_settings(key, value_json, updated_at)
            VALUES ('analytics.retention', CAST(@settings AS jsonb), CURRENT_TIMESTAMP)
            ON CONFLICT (key) DO UPDATE SET value_json = EXCLUDED.value_json, updated_at = EXCLUDED.updated_at
            """;
        AddParameter(command, "settings", JsonSerializer.Serialize(new { statistics_days = statisticsDays, security_event_days = securityEventDays }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AnalyticsRetention> ReadRetentionAsync(DbConnection connection, DbTransaction transaction,
        AnalyticsIngestionOptions options, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value_json::text FROM caddy_ui.application_settings WHERE key = 'analytics.retention'";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json)
            return new AnalyticsRetention(options.StatisticsRetentionDays, options.SecurityEventRetentionDays);
        using var settings = JsonDocument.Parse(json);
        return new AnalyticsRetention(Math.Clamp(settings.RootElement.GetProperty("statistics_days").GetInt32(), 1, 3650),
            Math.Clamp(settings.RootElement.GetProperty("security_event_days").GetInt32(), 1, 3650));
    }

    private static DateTimeOffset Bucket(DateTimeOffset value, int minutes)
    {
        var ticks = TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTimeOffset(value.UtcTicks / ticks * ticks, TimeSpan.Zero);
    }

    private static readonly string[] ScannerPaths = ["/.env", "/.git", "/wp-admin", "/wp-login", "/phpmyadmin", "/cgi-bin", "/actuator", "/vendor/phpunit", "/server-status", "/boaform"];

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
    private sealed record PreparedAnalyticsRequest(ClassifiedRequest Classified, AnalyticsClientIdentity? Identity);
}
