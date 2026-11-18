using Microsoft.Extensions.Configuration;

namespace CaddyUi.Infrastructure.Analytics;

public sealed class AnalyticsIngestionOptions
{
    public bool Enabled { get; init; }

    public IReadOnlyList<string> LogPaths { get; init; } = Array.Empty<string>();

    public int BatchSize { get; init; } = 500;

    public int PollIntervalMilliseconds { get; init; } = 1000;

    public int BacklogDelayMilliseconds { get; init; } = 100;

    public int FlushIntervalSeconds { get; init; } = 30;

    public int StatisticsRetentionDays { get; init; } = 90;

    public int SecurityEventRetentionDays { get; init; } = 30;

    public int SecurityEvidenceMinutes { get; init; } = 61;

    public int MaintenanceIntervalMinutes { get; init; } = 15;

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
                ReadInt32(configuration, "CADDY_UI_INGEST_BATCH_SIZE", section.GetValue("BatchSize", 500)),
                1,
                5000),
            PollIntervalMilliseconds = Clamp(
                ReadInt32(configuration, "CADDY_UI_INGEST_FLUSH_MS", section.GetValue("PollIntervalMilliseconds", 1000)),
                100,
                60000),
            BacklogDelayMilliseconds = Clamp(
                ReadInt32(
                    configuration,
                    "CADDY_UI_INGEST_BACKLOG_DELAY_MS",
                    section.GetValue("BacklogDelayMilliseconds", 100)),
                10,
                5000),
            FlushIntervalSeconds = Clamp(section.GetValue("FlushIntervalSeconds", 30), 1, 300),
            StatisticsRetentionDays = Clamp(section.GetValue("StatisticsRetentionDays", 90), 1, 3650),
            SecurityEventRetentionDays = Clamp(section.GetValue("SecurityEventRetentionDays", 30), 1, 3650),
            SecurityEvidenceMinutes = Clamp(Math.Max(configuration.GetValue("IpSecurity:RiskWindowMinutes", 30), configuration.GetValue("IpSecurity:DiscoveryLookbackMinutes", 60)), 1, 1440) + 1,
            MaintenanceIntervalMinutes = Clamp(section.GetValue("MaintenanceIntervalMinutes", 15), 5, 1440),
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
