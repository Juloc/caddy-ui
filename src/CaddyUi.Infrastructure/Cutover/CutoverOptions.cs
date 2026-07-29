using Microsoft.Extensions.Configuration;

namespace CaddyUi.Infrastructure.Cutover;

public sealed class CutoverOptions
{
    public bool Enabled { get; init; }

    public string LegacySqlitePath { get; init; } = "/data/caddy-ui/legacy/caddy-ui.db";

    public string LegacyStatisticsPath { get; init; } = "/data/caddy-ui/cutover/legacy-statistics.json";

    public string ManifestDirectory { get; init; } = "/data/caddy-ui/cutover";

    public int MinimumShadowHours { get; init; } = 24;

    public int MaximumBackupAgeHours { get; init; } = 24;

    public double MaximumMetricDifferencePercent { get; init; } = 5;

    public int AdminPort { get; init; } = 8098;

    public int PortalPort { get; init; } = 8099;

    public static CutoverOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Cutover");

        return new CutoverOptions
        {
            Enabled = ReadBoolean(configuration, "CADDY_UI_CUTOVER_ENABLED", section.GetValue("Enabled", false)),
            LegacySqlitePath = FullPath(
                configuration["CADDY_UI_LEGACY_SQLITE_PATH"] ?? section["LegacySqlitePath"],
                "/data/caddy-ui/legacy/caddy-ui.db"),
            LegacyStatisticsPath = FullPath(
                configuration["CADDY_UI_LEGACY_STATISTICS_PATH"] ?? section["LegacyStatisticsPath"],
                "/data/caddy-ui/cutover/legacy-statistics.json"),
            ManifestDirectory = FullPath(
                configuration["CADDY_UI_CUTOVER_MANIFEST_DIR"] ?? section["ManifestDirectory"],
                "/data/caddy-ui/cutover"),
            MinimumShadowHours = Math.Clamp(
                ReadInt32(configuration, "CADDY_UI_MINIMUM_SHADOW_HOURS", section.GetValue("MinimumShadowHours", 24)),
                1,
                24 * 30),
            MaximumBackupAgeHours = Math.Clamp(
                ReadInt32(configuration, "CADDY_UI_MAXIMUM_BACKUP_AGE_HOURS", section.GetValue("MaximumBackupAgeHours", 24)),
                1,
                24 * 30),
            MaximumMetricDifferencePercent = Math.Clamp(
                ReadDouble(
                    configuration,
                    "CADDY_UI_MAXIMUM_METRIC_DIFFERENCE_PERCENT",
                    section.GetValue("MaximumMetricDifferencePercent", 5d)),
                0,
                100),
            AdminPort = Math.Clamp(section.GetValue("AdminPort", 8098), 1, 65535),
            PortalPort = Math.Clamp(section.GetValue("PortalPort", 8099), 1, 65535),
        };
    }

    private static string FullPath(string? value, string fallback)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? fallback : value.Trim());
    }

    private static bool ReadBoolean(IConfiguration configuration, string key, bool fallback)
    {
        return bool.TryParse(configuration[key], out var value) ? value : fallback;
    }

    private static int ReadInt32(IConfiguration configuration, string key, int fallback)
    {
        return int.TryParse(
            configuration[key],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
                ? value
                : fallback;
    }

    private static double ReadDouble(IConfiguration configuration, string key, double fallback)
    {
        return double.TryParse(
            configuration[key],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
                ? value
                : fallback;
    }
}
