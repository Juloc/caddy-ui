using Microsoft.Extensions.Configuration;

namespace CaddyUi.Infrastructure.Security;

public enum IpBlockWriteMode
{
    Disabled,
    Shadow,
    Active,
}

public sealed class IpSecurityOptions
{
    public bool IntelligenceEnabled { get; init; }

    public Uri RipeStatBaseAddress { get; init; } = new("https://stat.ripe.net/");

    public int ProviderTimeoutSeconds { get; init; } = 5;

    public int SuccessCacheHours { get; init; } = 24;

    public int FailureCacheMinutes { get; init; } = 10;

    public int RefreshBatchSize { get; init; } = 20;

    public int RefreshIntervalSeconds { get; init; } = 15;

    public int DiscoveryLookbackMinutes { get; init; } = 60;

    public bool RiskAssessmentEnabled { get; init; }

    public int RiskWindowMinutes { get; init; } = 30;

    public int RiskRefreshMinutes { get; init; } = 5;

    public int RiskBatchSize { get; init; } = 100;

    public IpBlockWriteMode BlockWriteMode { get; init; } = IpBlockWriteMode.Disabled;

    public string BlocklistPath { get; init; } = string.Empty;

    public int MaximumBlockHours { get; init; } = 24 * 30;

    public static IpSecurityOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("IpSecurity");
        var baseAddressText = configuration["CADDY_UI_RIPESTAT_BASE_ADDRESS"] ??
            section["RipeStatBaseAddress"] ??
            "https://stat.ripe.net/";
        if (!Uri.TryCreate(baseAddressText, UriKind.Absolute, out var baseAddress) ||
            baseAddress.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "IpSecurity:RipeStatBaseAddress must be an absolute HTTPS URI.");
        }

        return new IpSecurityOptions
        {
            IntelligenceEnabled = ReadBoolean(
                configuration,
                "CADDY_UI_IP_INTELLIGENCE_ENABLED",
                section.GetValue("IntelligenceEnabled", false)),
            RipeStatBaseAddress = baseAddress,
            ProviderTimeoutSeconds = Clamp(
                ReadInt32(configuration, "CADDY_UI_IP_PROVIDER_TIMEOUT_SECONDS", section.GetValue("ProviderTimeoutSeconds", 5)),
                1,
                30),
            SuccessCacheHours = Clamp(
                ReadInt32(configuration, "CADDY_UI_IP_SUCCESS_CACHE_HOURS", section.GetValue("SuccessCacheHours", 24)),
                1,
                24 * 30),
            FailureCacheMinutes = Clamp(
                ReadInt32(configuration, "CADDY_UI_IP_FAILURE_CACHE_MINUTES", section.GetValue("FailureCacheMinutes", 10)),
                1,
                24 * 60),
            RefreshBatchSize = Clamp(section.GetValue("RefreshBatchSize", 20), 1, 500),
            RefreshIntervalSeconds = Clamp(section.GetValue("RefreshIntervalSeconds", 15), 1, 3600),
            DiscoveryLookbackMinutes = Clamp(section.GetValue("DiscoveryLookbackMinutes", 60), 1, 24 * 60),
            RiskAssessmentEnabled = ReadBoolean(
                configuration,
                "CADDY_UI_RISK_ASSESSMENT_ENABLED",
                section.GetValue("RiskAssessmentEnabled", false)),
            RiskWindowMinutes = Clamp(section.GetValue("RiskWindowMinutes", 30), 1, 24 * 60),
            RiskRefreshMinutes = Clamp(section.GetValue("RiskRefreshMinutes", 5), 1, 24 * 60),
            RiskBatchSize = Clamp(section.GetValue("RiskBatchSize", 100), 1, 1000),
            BlockWriteMode = ParseWriteMode(
                configuration["CADDY_UI_BLOCKLIST_WRITE_MODE"] ??
                section["BlockWriteMode"] ??
                "disabled"),
            BlocklistPath = Path.GetFullPath(
                configuration["CADDY_UI_BLOCKLIST_PATH"] ??
                section["BlocklistPath"] ??
                Path.Combine(Path.GetTempPath(), "caddy-ui-blocklist.shadow")),
            MaximumBlockHours = Clamp(section.GetValue("MaximumBlockHours", 24 * 30), 1, 24 * 365),
        };
    }

    private static IpBlockWriteMode ParseWriteMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "disabled" => IpBlockWriteMode.Disabled,
            "shadow" => IpBlockWriteMode.Shadow,
            "active" => IpBlockWriteMode.Active,
            _ => throw new InvalidOperationException(
                "CADDY_UI_BLOCKLIST_WRITE_MODE must be disabled, shadow or active."),
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
