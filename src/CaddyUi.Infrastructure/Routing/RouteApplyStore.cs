using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaddyUi.Application.Routing;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Routing;

public sealed class RouteApplyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public RouteApplyStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<RouteRevisionRecord> CreateRevisionAsync(
        CaddyCompilation compilation,
        string reason,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(actor);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var normalizedReason = Required(reason, 500, "Revision reason");
        var contentJson = JsonSerializer.Serialize(
            new { format = "caddyfile", content = compilation.Content },
            JsonOptions);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO caddy_ui.route_revisions(
                    id, created_at, actor_user_id, reason, manifest_json, content_json, digest, applied)
                VALUES(
                    @id, @now, @actor_user_id, @reason, CAST(@manifest_json AS jsonb),
                    CAST(@content_json AS jsonb), @digest, false)
                """;
            AddParameter(command, "id", id);
            AddParameter(command, "now", now);
            AddParameter(command, "actor_user_id", actor.UserId);
            AddParameter(command, "reason", normalizedReason);
            AddParameter(command, "manifest_json", compilation.ManifestJson);
            AddParameter(command, "content_json", contentJson);
            AddParameter(command, "digest", compilation.Digest);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "route-revision.create",
                "route_revision",
                id.ToString("D"),
                "{}",
                JsonSerializer.Serialize(new { compilation.Digest, reason = normalizedReason }, JsonOptions),
                "success",
                id,
                Guid.NewGuid().ToString("N"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return new RouteRevisionRecord(
            id,
            now,
            actor.UserId,
            actor.Username,
            normalizedReason,
            compilation.ManifestJson,
            compilation.Content,
            compilation.Digest,
            false);
    }

    public async Task<RouteRevisionRecord?> GetRevisionAsync(
        Guid revisionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revisions.id, revisions.created_at, revisions.actor_user_id,
                   COALESCE(users.username, 'system'), revisions.reason,
                   revisions.manifest_json::text, revisions.content_json::text,
                   revisions.digest, revisions.applied
            FROM caddy_ui.route_revisions AS revisions
            LEFT JOIN caddy_ui.users AS users ON users.id = revisions.actor_user_id
            WHERE revisions.id = @id
            LIMIT 1
            """;
        AddParameter(command, "id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    public async Task<RouteRevisionRecord?> GetRevisionByDigestAsync(
        string digest,
        CancellationToken cancellationToken = default)
    {
        var normalizedDigest = Required(digest, 128, "Revision digest");
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revisions.id, revisions.created_at, revisions.actor_user_id,
                   COALESCE(users.username, 'system'), revisions.reason,
                   revisions.manifest_json::text, revisions.content_json::text,
                   revisions.digest, revisions.applied
            FROM caddy_ui.route_revisions AS revisions
            LEFT JOIN caddy_ui.users AS users ON users.id = revisions.actor_user_id
            WHERE lower(revisions.digest) = lower(@digest)
            ORDER BY revisions.created_at DESC
            LIMIT 1
            """;
        AddParameter(command, "digest", normalizedDigest);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    public async Task<IReadOnlyList<RouteRevisionRecord>> ListRevisionsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revisions.id, revisions.created_at, revisions.actor_user_id,
                   COALESCE(users.username, 'system'), revisions.reason,
                   revisions.manifest_json::text, revisions.content_json::text,
                   revisions.digest, revisions.applied
            FROM caddy_ui.route_revisions AS revisions
            LEFT JOIN caddy_ui.users AS users ON users.id = revisions.actor_user_id
            ORDER BY revisions.created_at DESC
            LIMIT @limit
            """;
        AddParameter(command, "limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RouteRevisionRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRevision(reader));
        }

        return result;
    }

    public async Task<Guid> CreateSnapshotAsync(
        string content,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var contentJson = JsonSerializer.Serialize(new { format = "caddyfile", content }, JsonOptions);
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.caddy_snapshots(id, created_at, digest, manifest_json, content_json, reason)
            VALUES(@id, @now, @digest, '{}'::jsonb, CAST(@content_json AS jsonb), @reason)
            ON CONFLICT (digest) DO NOTHING
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "digest", digest);
                AddParameter(command, "content_json", contentJson);
                AddParameter(command, "reason", Limit(reason, 500));
            },
            cancellationToken);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT id FROM caddy_ui.caddy_snapshots WHERE digest = @digest LIMIT 1";
        AddParameter(lookup, "digest", digest);
        return (Guid)(await lookup.ExecuteScalarAsync(cancellationToken) ?? id);
    }

    public async Task<CaddySnapshotRecord?> GetSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created_at, digest, manifest_json::text, content_json::text, reason
            FROM caddy_ui.caddy_snapshots
            WHERE id = @id
            LIMIT 1
            """;
        AddParameter(command, "id", snapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CaddySnapshotRecord(
            reader.GetGuid(0),
            ReadTimestamp(reader, 1),
            reader.GetString(2),
            reader.GetString(3),
            ReadContent(reader.GetString(4)),
            reader.GetString(5));
    }

    public async Task<Guid> StartOperationAsync(
        Guid? revisionId,
        ManagementActor actor,
        string correlationId,
        Guid? previousSnapshotId,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO caddy_ui.apply_operations(
                id, route_revision_id, actor_user_id, correlation_id, state,
                started_at, completed_at, error, previous_snapshot_id)
            VALUES(
                @id, @revision_id, @actor_user_id, @correlation_id, 'running',
                @started_at, NULL, '', @previous_snapshot_id)
            """,
            command =>
            {
                AddParameter(command, "id", id);
                AddParameter(command, "revision_id", revisionId);
                AddParameter(command, "actor_user_id", actor.UserId);
                AddParameter(command, "correlation_id", correlationId);
                AddParameter(command, "started_at", DateTimeOffset.UtcNow);
                AddParameter(command, "previous_snapshot_id", previousSnapshotId);
            },
            cancellationToken);
        return id;
    }

    public Task RecordOperationStepAsync(
        Guid operationId,
        int sequence,
        string name,
        string state,
        string detailsJson,
        string error,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            """
            INSERT INTO caddy_ui.apply_operation_steps(
                operation_id, sequence, name, state, started_at, completed_at, details_json, error)
            VALUES(
                @operation_id, @sequence, @name, @state, @now, @now,
                CAST(@details_json AS jsonb), @error)
            ON CONFLICT (operation_id, sequence) DO UPDATE SET
                state = EXCLUDED.state,
                completed_at = EXCLUDED.completed_at,
                details_json = EXCLUDED.details_json,
                error = EXCLUDED.error
            """,
            command =>
            {
                AddParameter(command, "operation_id", operationId);
                AddParameter(command, "sequence", sequence);
                AddParameter(command, "name", Limit(name, 120));
                AddParameter(command, "state", Limit(state, 24));
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "details_json", NormalizeObjectJson(detailsJson));
                AddParameter(command, "error", Limit(error, 4000));
            },
            cancellationToken);
    }

    public async Task CompleteOperationAsync(
        Guid operationId,
        Guid? revisionId,
        string state,
        string error,
        ManagementActor actor,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE caddy_ui.apply_operations
                    SET state = @state, completed_at = @now, error = @error
                    WHERE id = @id
                    """;
                AddParameter(command, "state", Limit(state, 24));
                AddParameter(command, "now", DateTimeOffset.UtcNow);
                AddParameter(command, "error", Limit(error, 4000));
                AddParameter(command, "id", operationId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (revisionId is Guid appliedRevision && state == "applied")
            {
                await using var revision = connection.CreateCommand();
                revision.Transaction = transaction;
                revision.CommandText =
                    "UPDATE caddy_ui.route_revisions SET applied = (id = @id) WHERE applied OR id = @id";
                AddParameter(revision, "id", appliedRevision);
                await revision.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(
                connection,
                transaction,
                actor,
                "caddy.apply",
                "apply_operation",
                operationId.ToString("D"),
                "{}",
                JsonSerializer.Serialize(new { state, error = Limit(error, 4000) }, JsonOptions),
                state,
                revisionId,
                operationId.ToString("N"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ApplyOperationRecord>> ListOperationsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, route_revision_id, correlation_id, state, started_at,
                   completed_at, error, previous_snapshot_id
            FROM caddy_ui.apply_operations
            ORDER BY started_at DESC
            LIMIT @limit
            """;
        AddParameter(command, "limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ApplyOperationRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ApplyOperationRecord(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadTimestamp(reader, 4),
                reader.IsDBNull(5) ? null : ReadTimestamp(reader, 5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7)));
        }

        return result;
    }

    public async Task<ApplyOperationRecord?> GetLatestAppliedOperationAsync(
        CancellationToken cancellationToken = default)
    {
        var operations = await ListOperationsAsync(100, cancellationToken);
        return operations.FirstOrDefault(operation =>
            operation.State == "applied" && operation.PreviousSnapshotId is not null);
    }

    private static RouteRevisionRecord ReadRevision(DbDataReader reader)
    {
        return new RouteRevisionRecord(
            reader.GetGuid(0),
            ReadTimestamp(reader, 1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ReadContent(reader.GetString(6)),
            reader.GetString(7),
            reader.GetBoolean(8));
    }

    private static string ReadContent(string contentJson)
    {
        using var document = JsonDocument.Parse(contentJson);
        return document.RootElement.TryGetProperty("content", out var content)
            ? content.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task InsertAuditAsync(
        DbConnection connection,
        DbTransaction transaction,
        ManagementActor actor,
        string action,
        string objectType,
        string objectId,
        string beforeJson,
        string afterJson,
        string result,
        Guid? revisionId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO caddy_ui.audit_events(
                occurred_at, actor_user_id, actor_username, remote_address,
                action, object_type, object_id, before_json, after_json,
                result, revision_id, correlation_id)
            VALUES(
                @occurred_at, @actor_user_id, @actor_username,
                NULLIF(@remote_address, '')::inet,
                @action, @object_type, @object_id,
                CAST(@before_json AS jsonb), CAST(@after_json AS jsonb),
                @result, @revision_id, @correlation_id)
            """;
        AddParameter(command, "occurred_at", DateTimeOffset.UtcNow);
        AddParameter(command, "actor_user_id", actor.UserId);
        AddParameter(command, "actor_username", Limit(actor.Username, 200));
        AddParameter(command, "remote_address", actor.RemoteAddress);
        AddParameter(command, "action", action);
        AddParameter(command, "object_type", objectType);
        AddParameter(command, "object_id", objectId);
        AddParameter(command, "before_json", NormalizeObjectJson(beforeJson));
        AddParameter(command, "after_json", NormalizeObjectJson(afterJson));
        AddParameter(command, "result", result);
        AddParameter(command, "revision_id", revisionId);
        AddParameter(command, "correlation_id", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteAsync(
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeObjectJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.GetRawText()
                : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
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

    private static string Limit(string value, int maximum)
    {
        return value.Length <= maximum ? value : value[..maximum];
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
