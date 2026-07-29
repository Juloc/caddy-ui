using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace CaddyUi.Migration;

public sealed partial class LegacyImportService
{
    private static LegacyTableMigrationResult PlanTable(
        LegacyTableInventory table,
        bool includeRawRequests)
    {
        if (EphemeralTables.Contains(table.Name))
        {
            return new LegacyTableMigrationResult(
                table.Name,
                table.RowCount,
                0,
                0,
                table.RowCount,
                string.Empty,
                "Will be skipped because sessions and previews are intentionally invalidated.");
        }

        if (RawRequestTables.Contains(table.Name) && !includeRawRequests)
        {
            return new LegacyTableMigrationResult(
                table.Name,
                table.RowCount,
                0,
                0,
                table.RowCount,
                string.Empty,
                "Will be skipped because raw-request import is disabled.");
        }

        var target = TargetTableFor(table.Name);
        return target is null
            ? new LegacyTableMigrationResult(
                table.Name,
                table.RowCount,
                0,
                table.RowCount,
                0,
                "legacy_source_rows",
                "Will be preserved row-for-row as JSON.")
            : new LegacyTableMigrationResult(
                table.Name,
                table.RowCount,
                table.RowCount,
                0,
                0,
                target,
                "Will be mapped to the phase-2 PostgreSQL schema.");
    }

    private static string? TargetTableFor(string tableName)
    {
        return tableName.ToLowerInvariant() switch
        {
            "users" => "users",
            "settings" => "application_settings",
            "providers" => "dns_providers",
            "routes" => "managed_routes",
            "access_groups" => "access_groups",
            "access_credentials" => "access_credentials",
            "revisions" => "route_revisions",
            "audit_events" => "audit_events",
            "notifications" => "notifications",
            "traffic_buckets" => "traffic_aggregates",
            "migration_state" => "legacy_migration_state",
            _ => null
        };
    }

    private static int GetImportOrder(LegacyTableInventory table)
    {
        return table.Name.ToLowerInvariant() switch
        {
            "users" => 10,
            "settings" => 20,
            "access_groups" => 30,
            "access_credentials" => 40,
            "providers" => 50,
            "routes" => 60,
            "revisions" => 70,
            "audit_events" => 80,
            "notifications" => 90,
            "traffic_buckets" => 100,
            "migration_state" => 110,
            _ => 1000
        };
    }

    private static IReadOnlyList<string> BuildInventoryWarnings(
        LegacyDatabaseInventory inventory)
    {
        var warnings = new List<string>();
        if (inventory.SchemaVersion > 2)
        {
            warnings.Add(
                $"SQLite schema version {inventory.SchemaVersion} is newer than the phase-2 importer contract.");
        }

        if (inventory.Tables.Count == 0)
        {
            warnings.Add("The SQLite database contains no application tables.");
        }

        return warnings;
    }

    private static string BuildSourceKey(
        LegacyTableInventory table,
        IReadOnlyDictionary<string, object?> row,
        long rowNumber,
        string payloadJson)
    {
        if (table.PrimaryKeyColumns.Count > 0)
        {
            return string.Join(
                "\u001F",
                table.PrimaryKeyColumns.Select(column => Text(row, column)));
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{rowNumber}:{Convert.ToHexStringLower(digest)}");
    }

    private static string SerializeRow(
        IReadOnlyDictionary<string, object?> row)
    {
        var ordered = row
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => NormalizeSqliteValue(item.Value),
                StringComparer.Ordinal);

        return JsonSerializer.Serialize(
            ordered,
            LegacyMigrationReport.JsonOptions);
    }

    private static object? NormalizeSqliteValue(object? value)
    {
        return value switch
        {
            null => null,
            DBNull _ => null,
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => value
        };
    }

    private static string NormalizeJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(value);
        }
    }

    private static string Text(
        IReadOnlyDictionary<string, object?> row,
        string key,
        string defaultValue = "")
    {
        if (!row.TryGetValue(key, out var value) || value is null || value is DBNull)
        {
            return defaultValue;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? defaultValue;
    }

    private static long Integer(
        IReadOnlyDictionary<string, object?> row,
        string key,
        long defaultValue = 0)
    {
        if (!row.TryGetValue(key, out var value) || value is null || value is DBNull)
        {
            return defaultValue;
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool Boolean(
        IReadOnlyDictionary<string, object?> row,
        string key,
        bool defaultValue)
    {
        if (!row.TryGetValue(key, out var value) || value is null || value is DBNull)
        {
            return defaultValue;
        }

        return value switch
        {
            bool boolean => boolean,
            long integer => integer != 0,
            int integer => integer != 0,
            string text when bool.TryParse(text, out var boolean) => boolean,
            string text when long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var integer) => integer != 0,
            _ => defaultValue
        };
    }

    private static DateTimeOffset Timestamp(
        IReadOnlyDictionary<string, object?> row,
        string key)
    {
        return NullableTimestamp(row, key) ?? DateTimeOffset.UnixEpoch;
    }

    private static DateTimeOffset? NullableTimestamp(
        IReadOnlyDictionary<string, object?> row,
        string key)
    {
        var value = Text(row, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static Guid LegacyGuid(string entity, string legacyId)
    {
        if (Guid.TryParse(legacyId, out var parsed))
        {
            return parsed;
        }

        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{entity}\u001F{legacyId}"));
        var bytes = digest[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static Guid? NullableLegacyGuid(
        string entity,
        string legacyId)
    {
        return string.IsNullOrWhiteSpace(legacyId)
            ? null
            : LegacyGuid(entity, legacyId);
    }

    private static byte[]? ProtectSecret(
        IDataProtector protector,
        string value)
    {
        ArgumentNullException.ThrowIfNull(protector);

        return string.IsNullOrWhiteSpace(value)
            ? null
            : Encoding.UTF8.GetBytes(protector.Protect(value));
    }

    private static string ValidChoice(
        string value,
        string fallback,
        params string[] choices)
    {
        return choices.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : fallback;
    }

    private sealed record ImportOutcome(
        string TargetTable,
        string TargetKey);
}
