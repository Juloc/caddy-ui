using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Security;

public sealed record ClientSecuritySummary(
    Guid Id,
    string ClientKey,
    bool Estimated,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string LatestAddress,
    long RequestCount,
    string Classification,
    int AutomationScore,
    string RiskLevel,
    DateTimeOffset? AssessedAt);

public sealed record ClientAssessmentReasonRecord(
    string Code,
    string Message,
    int Weight,
    string EvidenceJson);

public sealed record ClientSecurityDetails(
    ClientSecuritySummary Summary,
    IpIntelligenceSnapshot Intelligence,
    IReadOnlyList<ClientAssessmentReasonRecord> AssessmentReasons,
    IReadOnlyList<IpBlockRuleRecord> BlockRules);

public sealed class ClientSecurityQueryStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;
    private readonly IpIntelligenceStore _intelligenceStore;

    public ClientSecurityQueryStore(
        IDbContextFactory<CaddyUiDbContext> contextFactory,
        IpIntelligenceStore intelligenceStore)
    {
        _contextFactory = contextFactory;
        _intelligenceStore = intelligenceStore;
    }

    public async Task<IReadOnlyList<ClientSecuritySummary>> ListAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + "\n" +
            """
            ORDER BY clients.last_seen_at DESC
            LIMIT @limit
            """;
        AddParameter(command, "limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ClientSecuritySummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadSummary(reader));
        }

        return result;
    }

    public async Task<ClientSecurityDetails?> GetAsync(
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        var summary = await ReadSummaryAsync(connection, clientId, cancellationToken);
        if (summary is null)
        {
            return null;
        }

        var reasons = await ReadReasonsAsync(connection, clientId, cancellationToken);
        var blocks = summary.LatestAddress.Length == 0
            ? Array.Empty<IpBlockRuleRecord>()
            : await ReadBlocksAsync(connection, summary.LatestAddress, cancellationToken);
        var intelligence = IPAddress.TryParse(summary.LatestAddress, out var address)
            ? await _intelligenceStore.GetOrQueueAsync(address, cancellationToken)
            : new IpIntelligenceSnapshot(null, false, false);

        return new ClientSecurityDetails(summary, intelligence, reasons, blocks);
    }

    private async Task<ClientSecuritySummary?> ReadSummaryAsync(
        DbConnection connection,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + "\nWHERE clients.id = @client_id";
        AddParameter(command, "client_id", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSummary(reader) : null;
    }

    private static async Task<IReadOnlyList<ClientAssessmentReasonRecord>> ReadReasonsAsync(
        DbConnection connection,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT reasons.code, reasons.message, reasons.weight,
                   reasons.evidence_json::text
            FROM caddy_ui.client_assessment_reasons AS reasons
            JOIN caddy_ui.client_assessments AS assessments
              ON assessments.id = reasons.assessment_id
            WHERE assessments.id = (
                SELECT id
                FROM caddy_ui.client_assessments
                WHERE anonymous_client_id = @client_id
                ORDER BY created_at DESC
                LIMIT 1)
            ORDER BY reasons.sequence
            """;
        AddParameter(command, "client_id", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ClientAssessmentReasonRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new ClientAssessmentReasonRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<IpBlockRuleRecord>> ReadBlocksAsync(
        DbConnection connection,
        string address,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, address_or_network::text, reason, source, activation_state,
                   created_at, updated_at, expires_at, released_at,
                   created_by_user_id, correlation_id
            FROM caddy_ui.ip_block_rules
            WHERE CAST(@address AS inet) <<= address_or_network
            ORDER BY created_at DESC
            LIMIT 50
            """;
        AddParameter(command, "address", address);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<IpBlockRuleRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new IpBlockRuleRecord(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    ReadTimestamp(reader, 5),
                    ReadTimestamp(reader, 6),
                    reader.IsDBNull(7) ? null : ReadTimestamp(reader, 7),
                    reader.IsDBNull(8) ? null : ReadTimestamp(reader, 8),
                    reader.IsDBNull(9) ? null : reader.GetGuid(9),
                    reader.GetString(10)));
        }

        return result;
    }

    private static ClientSecuritySummary ReadSummary(DbDataReader reader)
    {
        return new ClientSecuritySummary(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetBoolean(2),
            ReadTimestamp(reader, 3),
            ReadTimestamp(reader, 4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : ReadTimestamp(reader, 10));
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

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private const string SummarySelect =
        """
        SELECT clients.id,
               clients.client_key,
               COALESCE((clients.metadata_json ->> 'estimated')::boolean, true),
               clients.first_seen_at,
               clients.last_seen_at,
               COALESCE(host(activity.remote_address), host(latest_assessment.remote_address), ''),
               COALESCE(activity.request_count, latest_assessment.request_count, 0),
               COALESCE(latest_assessment.classification, 'unknown'),
               COALESCE(latest_assessment.automation_score, 0),
               COALESCE(latest_assessment.risk, 'unknown'),
               latest_assessment.created_at
        FROM caddy_ui.anonymous_clients AS clients
        LEFT JOIN LATERAL (
            SELECT (array_agg(remote_address ORDER BY last_seen_at DESC) FILTER (WHERE remote_address IS NOT NULL))[1] AS remote_address,
                   SUM(request_count)::bigint AS request_count
            FROM caddy_ui.client_activity_buckets WHERE client_id = clients.id
        ) AS activity ON true
        LEFT JOIN LATERAL (
            SELECT classification, automation_score, risk, created_at, remote_address, request_count
            FROM caddy_ui.client_assessments AS assessments
            WHERE assessments.anonymous_client_id = clients.id
            ORDER BY assessments.created_at DESC
            LIMIT 1
        ) AS latest_assessment ON true
        """;
}
