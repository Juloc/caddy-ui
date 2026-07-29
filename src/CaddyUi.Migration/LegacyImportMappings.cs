using System.Data.Common;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace CaddyUi.Migration;

public sealed partial class LegacyImportService
{
    private static async Task<ImportOutcome> ImportUserAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, object?> row,
        IDataProtector totpProtector,
        CancellationToken cancellationToken)
    {
        var legacyId = Text(row, "id");
        var id = LegacyGuid("users", legacyId);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.users(
                id, username, display_name, password_hash, role, enabled,
                totp_secret_encrypted, totp_enabled, theme, created_at, updated_at, last_login_at)
            VALUES(
                @id, @username, @display_name, @password_hash, @role, @enabled,
                @totp_secret_encrypted, @totp_enabled, @theme, @created_at, @updated_at, @last_login_at)
            ON CONFLICT (id) DO UPDATE SET
                username = EXCLUDED.username,
                display_name = EXCLUDED.display_name,
                password_hash = EXCLUDED.password_hash,
                role = EXCLUDED.role,
                enabled = EXCLUDED.enabled,
                totp_secret_encrypted = EXCLUDED.totp_secret_encrypted,
                totp_enabled = EXCLUDED.totp_enabled,
                theme = EXCLUDED.theme,
                updated_at = EXCLUDED.updated_at,
                last_login_at = EXCLUDED.last_login_at
            """,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["username"] = Text(row, "username", legacyId),
                ["display_name"] = Text(row, "display_name", Text(row, "username", legacyId)),
                ["password_hash"] = Text(row, "password_hash"),
                ["role"] = ValidChoice(Text(row, "role"), "viewer", "admin", "editor", "viewer"),
                ["enabled"] = Boolean(row, "enabled", true),
                ["totp_secret_encrypted"] = ProtectSecret(
                    totpProtector,
                    Text(row, "totp_secret")),
                ["totp_enabled"] = Boolean(row, "totp_enabled", false),
                ["theme"] = ValidChoice(Text(row, "theme"), "system", "system", "light", "dark"),
                ["created_at"] = Timestamp(row, "created_at"),
                ["updated_at"] = Timestamp(row, "updated_at"),
                ["last_login_at"] = NullableTimestamp(row, "last_login_at")
            },
            cancellationToken);

        return new ImportOutcome("users", id.ToString("D"));
    }

    private static async Task<ImportOutcome> ImportSettingAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var key = Text(row, "key");

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.application_settings(key, value_json, updated_at)
            VALUES(@key, CAST(@value_json AS jsonb), @updated_at)
            ON CONFLICT (key) DO UPDATE SET
                value_json = EXCLUDED.value_json,
                updated_at = EXCLUDED.updated_at
            """,
            new Dictionary<string, object?>
            {
                ["key"] = key,
                ["value_json"] = NormalizeJson(Text(row, "value_json", "null")),
                ["updated_at"] = Timestamp(row, "updated_at")
            },
            cancellationToken);

        return new ImportOutcome("application_settings", key);
    }

    private static async Task<ImportOutcome> ImportProviderAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var id = LegacyGuid("providers", Text(row, "id"));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.dns_providers(
                id, provider_type, label, config_json, created_at, updated_at)
            VALUES(
                @id, @provider_type, @label, CAST(@config_json AS jsonb), @created_at, @updated_at)
            ON CONFLICT (id) DO UPDATE SET
                provider_type = EXCLUDED.provider_type,
                label = EXCLUDED.label,
                config_json = EXCLUDED.config_json,
                updated_at = EXCLUDED.updated_at
            """,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["provider_type"] = Text(row, "type", "unknown"),
                ["label"] = Text(row, "label", "Imported provider"),
                ["config_json"] = NormalizeJson(Text(row, "config_json", "{}")),
                ["created_at"] = Timestamp(row, "created_at"),
                ["updated_at"] = Timestamp(row, "updated_at")
            },
            cancellationToken);

        return new ImportOutcome("dns_providers", id.ToString("D"));
    }

    private static async Task<ImportOutcome> ImportRouteAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var id = LegacyGuid("routes", Text(row, "id"));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.managed_routes(
                id, name, host, kind, enabled, config_json, created_at, updated_at)
            VALUES(
                @id, @name, @host, @kind, @enabled, CAST(@config_json AS jsonb), @created_at, @updated_at)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                host = EXCLUDED.host,
                kind = EXCLUDED.kind,
                enabled = EXCLUDED.enabled,
                config_json = EXCLUDED.config_json,
                updated_at = EXCLUDED.updated_at
            """,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = Text(row, "name", id.ToString("D")),
                ["host"] = Text(row, "host"),
                ["kind"] = Text(row, "kind", "proxy"),
                ["enabled"] = Boolean(row, "enabled", true),
                ["config_json"] = NormalizeJson(Text(row, "config_json", "{}")),
                ["created_at"] = Timestamp(row, "created_at"),
                ["updated_at"] = Timestamp(row, "updated_at")
            },
            cancellationToken);

        return new ImportOutcome("managed_routes", id.ToString("D"));
    }

    private static async Task<ImportOutcome> ImportAccessGroupAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var id = LegacyGuid("access_groups", Text(row, "id"));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.access_groups(
                id, name, config_json, created_at, updated_at)
            VALUES(
                @id, @name, CAST(@config_json AS jsonb), @created_at, @updated_at)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                config_json = EXCLUDED.config_json,
                updated_at = EXCLUDED.updated_at
            """,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = Text(row, "name", id.ToString("D")),
                ["config_json"] = NormalizeJson(Text(row, "config_json", "{}")),
                ["created_at"] = Timestamp(row, "created_at"),
                ["updated_at"] = Timestamp(row, "updated_at")
            },
            cancellationToken);

        return new ImportOutcome("access_groups", id.ToString("D"));
    }

    private static async Task<ImportOutcome> ImportAccessCredentialAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var id = LegacyGuid("access_credentials", Text(row, "id"));
        var groupId = LegacyGuid("access_groups", Text(row, "group_id"));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.access_credentials(
                id, group_id, username, password_hash, enabled, created_at, updated_at)
            VALUES(
                @id, @group_id, @username, @password_hash, @enabled, @created_at, @updated_at)
            ON CONFLICT (id) DO UPDATE SET
                group_id = EXCLUDED.group_id,
                username = EXCLUDED.username,
                password_hash = EXCLUDED.password_hash,
                enabled = EXCLUDED.enabled,
                updated_at = EXCLUDED.updated_at
            """,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["group_id"] = groupId,
                ["username"] = Text(row, "username", id.ToString("D")),
                ["password_hash"] = Text(row, "password_hash"),
                ["enabled"] = Boolean(row, "enabled", true),
                ["created_at"] = Timestamp(row, "created_at"),
                ["updated_at"] = Timestamp(row, "updated_at")
            },
            cancellationToken);

        return new ImportOutcome("access_credentials", id.ToString("D"));
    }

    private static async Task<ImportOutcome> ImportRevisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var id = LegacyGuid("revisions", Text(row, "id"));
        var actorId = NullableLegacyGuid("users", Text(row, "actor_user_id"));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.route_revisions(
                id, created_at, actor_user_id, reason, manifest_json,
                content_json, digest, applied)
            VALUES(
                @id, @created_at, @actor_user_id, @reason,
                CAST(@manifest_json AS jsonb), CAST(@content_json AS jsonb),
                @digest, @applied)
            ON CONFLICT (id) DO UPDATE SET
                actor_user_id = EXCLUDED.actor_user_id,
                reason = EXCLUDED.reason,
                manifest_json = EXCLUDED.manifest_json,
                content_json = EXCLUDED.content_json,
                digest = EXCLUDED.digest,
                applied = EXCLUDED.applied
            """,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["created_at"] = Timestamp(row, "created_at"),
                ["actor_user_id"] = actorId,
                ["reason"] = Text(row, "reason", "Imported revision"),
                ["manifest_json"] = NormalizeJson(Text(row, "manifest_json", "{}")),
                ["content_json"] = NormalizeJson(Text(row, "content_json", "{}")),
                ["digest"] = Text(row, "digest", id.ToString("N")),
                ["applied"] = Boolean(row, "applied", false)
            },
            cancellationToken);

        return new ImportOutcome("route_revisions", id.ToString("D"));
    }

    private static async Task<ImportOutcome> ImportAuditEventAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sourceDigest,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var legacyId = Integer(row, "id");
        var actorId = NullableLegacyGuid("users", Text(row, "actor_user_id"));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.audit_events(
                occurred_at, actor_user_id, actor_username, remote_address,
                action, object_type, object_id, before_json, after_json,
                result, revision_id, correlation_id, legacy_source_digest, legacy_source_id)
            VALUES(
                @occurred_at, @actor_user_id, @actor_username,
                CAST(NULLIF(@remote_address, '') AS inet),
                @action, @object_type, @object_id,
                CAST(@before_json AS jsonb), CAST(@after_json AS jsonb),
                @result, @revision_id, @correlation_id, @source_digest, @legacy_id)
            ON CONFLICT DO NOTHING
            """,
            new Dictionary<string, object?>
            {
                ["occurred_at"] = Timestamp(row, "occurred_at"),
                ["actor_user_id"] = actorId,
                ["actor_username"] = Text(row, "actor_username"),
                ["remote_address"] = Text(row, "remote_address"),
                ["action"] = Text(row, "action"),
                ["object_type"] = Text(row, "object_type"),
                ["object_id"] = Text(row, "object_id"),
                ["before_json"] = NormalizeJson(Text(row, "before_json", "{}")),
                ["after_json"] = NormalizeJson(Text(row, "after_json", "{}")),
                ["result"] = Text(row, "result"),
                ["revision_id"] = NullableLegacyGuid("revisions", Text(row, "revision_id")),
                ["correlation_id"] = Text(row, "correlation_id"),
                ["source_digest"] = sourceDigest,
                ["legacy_id"] = legacyId
            },
            cancellationToken);

        return new ImportOutcome(
            "audit_events",
            $"{sourceDigest}:{legacyId.ToString(CultureInfo.InvariantCulture)}");
    }

    private static async Task<ImportOutcome> ImportNotificationAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sourceDigest,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var legacyId = Integer(row, "id");

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.notifications(
                created_at, severity, event_type, title, message,
                object_type, object_id, acknowledged_at,
                legacy_source_digest, legacy_source_id)
            VALUES(
                @created_at, @severity, @event_type, @title, @message,
                @object_type, @object_id, @acknowledged_at,
                @source_digest, @legacy_id)
            ON CONFLICT DO NOTHING
            """,
            new Dictionary<string, object?>
            {
                ["created_at"] = Timestamp(row, "created_at"),
                ["severity"] = Text(row, "severity", "info"),
                ["event_type"] = Text(row, "event_type"),
                ["title"] = Text(row, "title"),
                ["message"] = Text(row, "message"),
                ["object_type"] = Text(row, "object_type"),
                ["object_id"] = Text(row, "object_id"),
                ["acknowledged_at"] = NullableTimestamp(row, "acknowledged_at"),
                ["source_digest"] = sourceDigest,
                ["legacy_id"] = legacyId
            },
            cancellationToken);

        return new ImportOutcome(
            "notifications",
            $"{sourceDigest}:{legacyId.ToString(CultureInfo.InvariantCulture)}");
    }

    private static async Task<ImportOutcome> ImportTrafficBucketAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var granularity = Text(row, "granularity", "hour").ToLowerInvariant();
        var targetTable = granularity switch
        {
            "day" => "daily_traffic_aggregates",
            "month" => "monthly_traffic_aggregates",
            _ => "hourly_traffic_aggregates"
        };
        var bucketTimestamp = Timestamp(row, "bucket_start");
        var bucketValue = targetTable == "hourly_traffic_aggregates"
            ? (object)bucketTimestamp
            : DateOnly.FromDateTime(bucketTimestamp.UtcDateTime);

        var sql =
            $"""
             INSERT INTO caddy_ui.{targetTable}(
                 bucket_start, host, status_class, actor_type, request_type,
                 requests, page_views, bytes_sent, duration_sum_ms, duration_max_ms)
             VALUES(
                 @bucket_start, @host, @status_class, 'unknown', 'other',
                 @requests, 0, @bytes_sent, 0, 0)
             ON CONFLICT (bucket_start, host, status_class, actor_type, request_type)
             DO UPDATE SET
                 requests = EXCLUDED.requests,
                 bytes_sent = EXCLUDED.bytes_sent
             """;

        await ExecuteAsync(
            connection,
            transaction,
            sql,
            new Dictionary<string, object?>
            {
                ["bucket_start"] = bucketValue,
                ["host"] = Text(row, "host"),
                ["status_class"] = Text(row, "status_class"),
                ["requests"] = Integer(row, "requests"),
                ["bytes_sent"] = Integer(row, "bytes_sent")
            },
            cancellationToken);

        var targetKey = string.Join(
            ":",
            bucketTimestamp.ToString("O", CultureInfo.InvariantCulture),
            Text(row, "host"),
            Text(row, "status_class"));

        return new ImportOutcome(targetTable, targetKey);
    }

    private static async Task<ImportOutcome> ImportMigrationStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, object?> row,
        CancellationToken cancellationToken)
    {
        var sourceName = Text(row, "source");

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO caddy_ui.legacy_migration_state(
                source_name, imported_at, source_digest, original_payload_json)
            VALUES(
                @source_name, @imported_at, @source_digest, CAST(@payload AS jsonb))
            ON CONFLICT (source_name) DO UPDATE SET
                imported_at = EXCLUDED.imported_at,
                source_digest = EXCLUDED.source_digest,
                original_payload_json = EXCLUDED.original_payload_json
            """,
            new Dictionary<string, object?>
            {
                ["source_name"] = sourceName,
                ["imported_at"] = Timestamp(row, "imported_at"),
                ["source_digest"] = Text(row, "source_digest"),
                ["payload"] = SerializeRow(row)
            },
            cancellationToken);

        return new ImportOutcome(
            "legacy_migration_state",
            sourceName);
    }
}
