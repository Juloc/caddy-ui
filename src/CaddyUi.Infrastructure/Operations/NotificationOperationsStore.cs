using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaddyUi.Infrastructure.Operations;

public sealed class NotificationOperationsStore
{
    private readonly IDbContextFactory<CaddyUiDbContext> _contextFactory;

    public NotificationOperationsStore(IDbContextFactory<CaddyUiDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<NotificationChannelRecord>> ListNotificationChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await RelationalStoreSupport.OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, channel_type, enabled, config_json::text,
                   secret_references_json::text, last_tested_at,
                   last_test_status, last_test_error, updated_at
            FROM caddy_ui.notification_channels
            ORDER BY enabled DESC, lower(name)
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NotificationChannelRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new NotificationChannelRecord(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : ReadTimestamp(reader, 6),
                reader.GetString(7), reader.GetString(8), ReadTimestamp(reader, 9)));
        }

        return result;
    }

    public async Task<Guid> CreateNotificationChannelAsync(
        string name,
        string channelType,
        string configJson,
        string secretReferencesJson,
        CancellationToken cancellationToken = default)
    {
        var type = channelType.Trim().ToLowerInvariant();
        if (type is not ("email" or "webhook" or "discord" or "telegram"))
        {
            throw new ArgumentException("Unsupported notification channel type.", nameof(channelType));
        }

        var id = Guid.NewGuid();
        await RelationalStoreSupport.ExecuteNonQueryAsync(
            _contextFactory,
            """
            INSERT INTO caddy_ui.notification_channels(
                id, name, channel_type, enabled, config_json, secret_references_json,
                created_at, updated_at)
            VALUES(@id, @name, @channel_type, true, CAST(@config_json AS jsonb),
                   CAST(@secret_references_json AS jsonb), @now, @now)
            """,
            command =>
            {
                RelationalStoreSupport.AddParameter(command, "id", id);
                RelationalStoreSupport.AddParameter(command, "name", Required(name, 120, "Channel name"));
                RelationalStoreSupport.AddParameter(command, "channel_type", type);
                RelationalStoreSupport.AddParameter(command, "config_json", NormalizeObjectJson(configJson));
                RelationalStoreSupport.AddParameter(command, "secret_references_json", NormalizeObjectJson(secretReferencesJson));
                RelationalStoreSupport.AddParameter(command, "now", DateTimeOffset.UtcNow);
            },
            cancellationToken);
        return id;
    }

    public Task SetNotificationChannelEnabledAsync(
        Guid channelId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return RelationalStoreSupport.ExecuteNonQueryAsync(
            _contextFactory,
            "UPDATE caddy_ui.notification_channels SET enabled = @enabled, updated_at = @now WHERE id = @id",
            command =>
            {
                RelationalStoreSupport.AddParameter(command, "enabled", enabled);
                RelationalStoreSupport.AddParameter(command, "now", DateTimeOffset.UtcNow);
                RelationalStoreSupport.AddParameter(command, "id", channelId);
            },
            cancellationToken);
    }

    public Task RecordNotificationChannelTestAsync(
        Guid channelId,
        ProviderOperationResult result,
        CancellationToken cancellationToken = default)
    {
        return RelationalStoreSupport.ExecuteNonQueryAsync(
            _contextFactory,
            """
            UPDATE caddy_ui.notification_channels
            SET last_tested_at = @now, last_test_status = @status,
                last_test_error = @error, updated_at = @now
            WHERE id = @id
            """,
            command =>
            {
                RelationalStoreSupport.AddParameter(command, "now", DateTimeOffset.UtcNow);
                RelationalStoreSupport.AddParameter(command, "status", result.Succeeded ? "ok" : "failed");
                RelationalStoreSupport.AddParameter(command, "error", result.Succeeded ? string.Empty : Limit(result.Message, 2000));
                RelationalStoreSupport.AddParameter(command, "id", channelId);
            },
            cancellationToken);
    }

    public Task InsertNotificationAsync(
        SystemNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return RelationalStoreSupport.ExecuteNonQueryAsync(
            _contextFactory,
            """
            INSERT INTO caddy_ui.notifications(
                created_at, severity, event_type, title, message, object_type, object_id)
            VALUES(@now, @severity, @event_type, @title, @message, @object_type, @object_id)
            """,
            command =>
            {
                RelationalStoreSupport.AddParameter(command, "now", DateTimeOffset.UtcNow);
                RelationalStoreSupport.AddParameter(command, "severity", Limit(notification.Severity, 16));
                RelationalStoreSupport.AddParameter(command, "event_type", Limit(notification.EventType, 120));
                RelationalStoreSupport.AddParameter(command, "title", Limit(notification.Title, 300));
                RelationalStoreSupport.AddParameter(command, "message", Limit(notification.Message, 4000));
                RelationalStoreSupport.AddParameter(command, "object_type", Limit(notification.ObjectType, 120));
                RelationalStoreSupport.AddParameter(command, "object_id", Limit(notification.ObjectId, 300));
            },
            cancellationToken);
    }

    private static string NormalizeObjectJson(string? value)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The value must be a JSON object.", nameof(value));
        }

        return document.RootElement.GetRawText();
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

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

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
}
