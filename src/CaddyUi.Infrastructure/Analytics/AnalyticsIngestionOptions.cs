using Microsoft.Extensions.Configuration;

namespace CaddyUi.Infrastructure.Analytics;

public sealed class AnalyticsIngestionOptions
{
    public bool Enabled { get; init; }

    public IReadOnlyList<string> LogPaths { get; init; } = Array.Empty<string>();

    public int BatchSize { get; init; } = 1000;

    public int PollIntervalMilliseconds { get; init; } = 1000;

    public int SessionIdleMinutes { get; init; } = 30;

    public int PageLoadWindowSeconds { get; init; } = 15;

    public int RawRequestRetentionDays { get; init; } = 30;

    public int PageViewRetentionDays { get; init; } = 180;

    public int HourlyRetentionDays { get; init; } = 90;

    public int DailyRetentionDays { get; init; } = 730;

    public int MaintenanceIntervalMinutes { get; init; } = 360;

    public static AnalyticsIngestionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Analytics");
        var configuredPaths = section.GetSection("LogPaths").Get<string[]>() ?? Array.Empty<string>();
        var environmentPaths = configuration["CADDY_UI_LOG_PATHS"];
        var paths = string.IsNullOrWhiteSpace(environmentPaths)
            ? configuredPaths
            : environmentPaths.Split(
                [';', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new AnalyticsIngestionOptions
        {
            Enabled = ReadBoolean(
                configuration,
                "CADDY_UI_ANALYTICS_ENABLED",
                section.GetValue("Enabled", false)),
            LogPaths = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path.Trim()))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            BatchSize = Clamp(
                ReadInt32(configuration, "CADDY_UI_INGEST_BATCH_SIZE", section.GetValue("BatchSize", 1000)),
                1,
                10000),
            PollIntervalMilliseconds = Clamp(
                ReadInt32(configuration, "CADDY_UI_INGEST_FLUSH_MS", section.GetValue("PollIntervalMilliseconds", 1000)),
                100,
                60000),
            SessionIdleMinutes = Clamp(
                ReadInt32(configuration, "CADDY_UI_SESSION_IDLE_MINUTES", section.GetValue("SessionIdleMinutes", 30)),
                1,
                1440),
            PageLoadWindowSeconds = Clamp(section.GetValue("PageLoadWindowSeconds", 15), 1, 300),
            RawRequestRetentionDays = Clamp(
                ReadInt32(configuration, "CADDY_UI_RAW_REQUEST_RETENTION_DAYS", section.GetValue("RawRequestRetentionDays", 30)),
                1,
                3650),
            PageViewRetentionDays = Clamp(
                ReadInt32(configuration, "CADDY_UI_PAGEVIEW_RETENTION_DAYS", section.GetValue("PageViewRetentionDays", 180)),
                1,
                3650),
            HourlyRetentionDays = Clamp(section.GetValue("HourlyRetentionDays", 90), 1, 3650),
            DailyRetentionDays = Clamp(section.GetValue("DailyRetentionDays", 730), 1, 36500),
            MaintenanceIntervalMinutes = Clamp(section.GetValue("MaintenanceIntervalMinutes", 360), 5, 1440),
        };
    }

    private static bool ReadBoolean(
        IConfiguration configuration,
        string key,
        bool fallback)
    {
        return bool.TryParse(configuration[key], out var value)
            ? value
            : fallback;
    }

    private static int ReadInt32(
        IConfiguration configuration,
        string key,
        int fallback)
    {
        return int.TryParse(
            configuration[key],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
                ? value
                : fallback;
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        return Math.Clamp(value, minimum, maximum);
    }
}
