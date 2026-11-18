using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CaddyUi.Application.Security;
using CaddyUi.Domain.Security;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Security;

public sealed class ClientRiskAssessmentStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly ClientRiskEngine _engine;
    private readonly IpSecurityOptions _options;
    private readonly TimeProvider _timeProvider;

    public ClientRiskAssessmentStore(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        ClientRiskEngine engine,
        IpSecurityOptions options,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory;
        _engine = engine;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<int> AssessReadyClientsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.RiskAssessmentEnabled)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var windowStart = now.AddMinutes(-_options.RiskWindowMinutes);
        var refreshCutoff = now.AddMinutes(-_options.RiskRefreshMinutes);
        var candidates = await ListCandidatesAsync(
            windowStart,
            refreshCutoff,
            cancellationToken);
        var stored = 0;
        foreach (var candidate in candidates)
        {
            var sample = await LoadSampleAsync(
                candidate.ClientId,
                windowStart,
                now,
                cancellationToken);
            if (sample is null)
            {
                continue;
            }

            var assessment = _engine.Assess(sample);
            await SaveAssessmentAsync(
                candidate.ClientId,
                candidate.Address,
                sample.RequestCount,
                assessment,
                cancellationToken);
            stored++;
        }

        return stored;
    }

    private async Task<IReadOnlyList<ClientCandidate>> ListCandidatesAsync(
        DateTimeOffset windowStart,
        DateTimeOffset refreshCutoff,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT requests.client_id,
                   (array_agg(requests.remote_address ORDER BY requests.last_seen_at DESC)
                       FILTER (WHERE requests.remote_address IS NOT NULL))[1] AS remote_address
            FROM caddy_ui.client_activity_buckets AS requests
            WHERE requests.last_seen_at >= @window_start
              AND NOT EXISTS (
                  SELECT 1
                  FROM caddy_ui.client_assessments AS assessments
                  WHERE assessments.anonymous_client_id = requests.client_id
                    AND assessments.engine_version = @engine_version
                    AND assessments.created_at >= @refresh_cutoff)
            GROUP BY requests.client_id
            ORDER BY MAX(requests.last_seen_at) DESC
            LIMIT @limit
            """;
        AddParameter(command, "window_start", windowStart);
        AddParameter(command, "engine_version", ClientRiskEngine.CurrentVersion);
        AddParameter(command, "refresh_cutoff", refreshCutoff);
        AddParameter(command, "limit", _options.RiskBatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ClientCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new ClientCandidate(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : ReadAddress(reader, 1)));
        }

        return result;
    }

    private async Task<ClientRiskSample?> LoadSampleAsync(
        Guid clientId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH filtered AS (
                SELECT first_seen_at, last_seen_at, user_agent, is_bot, is_human, request_count, not_found_count,
                       auth_failure_count, unsafe_method_count, scanner_count, gap_count, gap_sum, gap_squares, path_hashes, hosts
                FROM caddy_ui.client_activity_buckets
                WHERE client_id = @client_id AND last_seen_at >= @window_start AND first_seen_at <= @window_end
            ), ordered AS (
                SELECT *,
                       EXTRACT(EPOCH FROM first_seen_at - LAG(last_seen_at) OVER (ORDER BY first_seen_at)) AS bridge
                FROM filtered
            ), timing AS (
                SELECT COALESCE(SUM(gap_count), 0) + COUNT(bridge) AS gap_count,
                       COALESCE(SUM(gap_sum), 0) + COALESCE(SUM(bridge), 0) AS gap_sum,
                       COALESCE(SUM(gap_squares), 0) + COALESCE(SUM(bridge * bridge), 0) AS gap_squares
                FROM ordered
            )
            SELECT COALESCE(SUM(request_count), 0)::bigint,
                   MIN(first_seen_at), MAX(last_seen_at),
                   (SELECT COUNT(DISTINCT value)::integer FROM filtered CROSS JOIN LATERAL jsonb_array_elements_text(path_hashes)),
                   (SELECT COUNT(DISTINCT value)::integer FROM filtered CROSS JOIN LATERAL jsonb_array_elements_text(hosts)),
                   COALESCE(SUM(not_found_count), 0)::bigint,
                   COALESCE(SUM(auth_failure_count), 0)::bigint,
                   LEAST(COALESCE(SUM(unsafe_method_count), 0), 2147483647)::integer,
                   BOOL_OR(is_bot), BOOL_OR(is_human),
                   COALESCE(MAX(NULLIF(user_agent, '')), ''),
                   (SELECT COALESCE(gap_sum / NULLIF(gap_count, 0), 0)::double precision FROM timing),
                   (SELECT SQRT(GREATEST(0, COALESCE(gap_squares / NULLIF(gap_count, 0) - POWER(gap_sum / NULLIF(gap_count, 0), 2), 0)))::double precision FROM timing),
                   LEAST(COALESCE(SUM(scanner_count), 0), 2147483647)::integer
            FROM filtered
            """;
        AddParameter(command, "client_id", clientId);
        AddParameter(command, "window_start", windowStart);
        AddParameter(command, "window_end", windowEnd);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt64(0) == 0)
        {
            return null;
        }

        var requestCount = reader.GetInt64(0);
        var sampleStartedAt = ReadTimestamp(reader, 1);
        var sampleEndedAt = ReadTimestamp(reader, 2);
        var distinctPaths = reader.GetInt32(3);
        var hostCount = reader.GetInt32(4);
        var notFoundCount = reader.GetInt64(5);
        var authenticationFailureCount = reader.GetInt64(6);
        var unsafeMethodCount = reader.GetInt32(7);
        var existingBot = reader.GetBoolean(8);
        var existingHuman = reader.GetBoolean(9);
        var userAgent = reader.GetString(10);
        var averageGap = reader.GetDouble(11);
        var standardDeviation = reader.GetDouble(12);
        var regularity = averageGap <= 0
            ? 0
            : Math.Clamp(1 - (standardDeviation / averageGap), 0, 1);
        var scannerPathCount = reader.GetInt32(13);

        return new ClientRiskSample(
            existingBot ? "bot" : existingHuman ? "human" : "unknown",
            userAgent,
            requestCount,
            sampleEndedAt - sampleStartedAt,
            regularity,
            distinctPaths,
            scannerPathCount,
            notFoundCount / (double)requestCount,
            authenticationFailureCount / (double)requestCount,
            unsafeMethodCount,
            hostCount,
            existingBot,
            sampleStartedAt,
            sampleEndedAt);
    }

    private async Task SaveAssessmentAsync(
        Guid clientId,
        IPAddress? address,
        long requestCount,
        ClientRiskAssessment assessment,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var assessmentId = Guid.NewGuid();
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO caddy_ui.client_assessments(
                    id, anonymous_client_id, remote_address, classification,
                    automation_score, risk, engine_version, sample_started_at,
                    sample_ended_at, created_at, request_count, sample_json)
                VALUES(
                    @id, @client_id, @remote_address, @classification,
                    @automation_score, @risk, @engine_version, @sample_started_at,
                    @sample_ended_at, @created_at, @request_count,
                    CAST(@sample_json AS jsonb))
                """,
                command =>
                {
                    AddParameter(command, "id", assessmentId);
                    AddParameter(command, "client_id", clientId);
                    AddParameter(command, "remote_address", address);
                    AddParameter(command, "classification", assessment.Classification);
                    AddParameter(command, "automation_score", assessment.AutomationScore);
                    AddParameter(command, "risk", assessment.RiskLevel.ToStorageValue());
                    AddParameter(command, "engine_version", assessment.EngineVersion);
                    AddParameter(command, "sample_started_at", assessment.SampleStartedAt);
                    AddParameter(command, "sample_ended_at", assessment.SampleEndedAt);
                    AddParameter(command, "created_at", _timeProvider.GetUtcNow());
                    AddParameter(command, "request_count", requestCount);
                    AddParameter(
                        command,
                        "sample_json",
                        JsonSerializer.Serialize(new Dictionary<string, object>
                        {
                            ["reasonCount"] = assessment.Reasons.Count,
                            ["engineVersion"] = assessment.EngineVersion,
                        }));
                },
                cancellationToken);

            for (var index = 0; index < assessment.Reasons.Count; index++)
            {
                var reason = assessment.Reasons[index];
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO caddy_ui.client_assessment_reasons(
                        assessment_id, sequence, code, message, weight, evidence_json)
                    VALUES(
                        @assessment_id, @sequence, @code, @message, @weight,
                        CAST(@evidence_json AS jsonb))
                    """,
                    command =>
                    {
                        AddParameter(command, "assessment_id", assessmentId);
                        AddParameter(command, "sequence", index);
                        AddParameter(command, "code", reason.Code);
                        AddParameter(command, "message", reason.Message);
                        AddParameter(command, "weight", reason.Weight);
                        AddParameter(command, "evidence_json", JsonSerializer.Serialize(reason.Evidence));
                    },
                    cancellationToken);
            }

            if (assessment.RiskLevel is ClientRiskLevel.Medium or ClientRiskLevel.High)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO caddy_ui.security_events(
                        occurred_at, kind, reason, remote_address,
                        anonymous_client_id, host, path, details_json)
                    VALUES(
                        @occurred_at, 'client-risk-assessment', @reason,
                        @remote_address, @client_id, '', '', CAST(@details_json AS jsonb))
                    """,
                    command =>
                    {
                        AddParameter(command, "occurred_at", _timeProvider.GetUtcNow());
                        AddParameter(
                            command,
                            "reason",
                            $"Client risk assessed as {assessment.RiskLevel.ToStorageValue()}.");
                        AddParameter(command, "remote_address", address);
                        AddParameter(command, "client_id", clientId);
                        AddParameter(
                            command,
                            "details_json",
                            JsonSerializer.Serialize(new Dictionary<string, object>
                            {
                                ["score"] = assessment.AutomationScore,
                                ["classification"] = assessment.Classification,
                                ["engineVersion"] = assessment.EngineVersion,
                                ["reasonCodes"] = assessment.Reasons.Select(reason => reason.Code).ToArray(),
                            }));
                    },
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static IPAddress? ReadAddress(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            IPAddress address => IpAddressClassifier.Normalize(address),
            string text when IPAddress.TryParse(text.Split('/')[0], out var address) =>
                IpAddressClassifier.Normalize(address),
            _ => null,
        };
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

    private sealed record ClientCandidate(Guid ClientId, IPAddress? Address);
}
