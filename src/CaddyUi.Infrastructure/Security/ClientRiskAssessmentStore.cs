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
    private static readonly string[] ScannerPathFragments =
    [
        "/.env",
        "/.git",
        "/wp-admin",
        "/wp-login",
        "/phpmyadmin",
        "/cgi-bin",
        "/actuator",
        "/vendor/phpunit",
        "/server-status",
        "/boaform",
    ];

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
            SELECT requests.anonymous_client_id,
                   (array_agg(requests.remote_address ORDER BY requests.occurred_at DESC)
                       FILTER (WHERE requests.remote_address IS NOT NULL))[1] AS remote_address
            FROM caddy_ui.request_events AS requests
            WHERE requests.anonymous_client_id IS NOT NULL
              AND requests.occurred_at >= @window_start
              AND NOT EXISTS (
                  SELECT 1
                  FROM caddy_ui.client_assessments AS assessments
                  WHERE assessments.anonymous_client_id = requests.anonymous_client_id
                    AND assessments.engine_version = @engine_version
                    AND assessments.created_at >= @refresh_cutoff)
            GROUP BY requests.anonymous_client_id
            ORDER BY MAX(requests.occurred_at) DESC
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
                SELECT occurred_at, actor_type, user_agent, path, status, method, host
                FROM caddy_ui.request_events
                WHERE anonymous_client_id = @client_id
                  AND occurred_at >= @window_start
                  AND occurred_at <= @window_end
            ), ordered AS (
                SELECT *,
                       EXTRACT(EPOCH FROM occurred_at - LAG(occurred_at)
                           OVER (ORDER BY occurred_at)) AS gap_seconds
                FROM filtered
            )
            SELECT COUNT(*)::bigint,
                   MIN(occurred_at),
                   MAX(occurred_at),
                   COUNT(DISTINCT path)::integer,
                   COUNT(DISTINCT host)::integer,
                   COUNT(*) FILTER (WHERE status = 404)::bigint,
                   COUNT(*) FILTER (WHERE status IN (401, 403))::bigint,
                   COUNT(*) FILTER (WHERE method NOT IN ('GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'))::integer,
                   BOOL_OR(actor_type = 'bot'),
                   BOOL_OR(actor_type = 'human'),
                   COALESCE(MAX(NULLIF(user_agent, '')), ''),
                   COALESCE(AVG(gap_seconds) FILTER (WHERE gap_seconds IS NOT NULL), 0)::double precision,
                   COALESCE(STDDEV_POP(gap_seconds) FILTER (WHERE gap_seconds IS NOT NULL), 0)::double precision
            FROM ordered
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
        var scannerPathCount = await CountScannerPathsAsync(
            connection,
            clientId,
            windowStart,
            windowEnd,
            cancellationToken);

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

    private static async Task<int> CountScannerPathsAsync(
        DbConnection connection,
        Guid clientId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var predicates = ScannerPathFragments
            .Select((_, index) => $"path ILIKE @scanner_path_{index}")
            .ToArray();
        command.CommandText =
            $"""
            SELECT COUNT(*)::integer
            FROM caddy_ui.request_events
            WHERE anonymous_client_id = @client_id
              AND occurred_at >= @window_start
              AND occurred_at <= @window_end
              AND ({string.Join(" OR ", predicates)})
            """;
        AddParameter(command, "client_id", clientId);
        AddParameter(command, "window_start", windowStart);
        AddParameter(command, "window_end", windowEnd);
        for (var index = 0; index < ScannerPathFragments.Length; index++)
        {
            AddParameter(command, $"scanner_path_{index}", $"%{ScannerPathFragments[index]}%");
        }

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
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
