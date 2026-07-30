using Microsoft.Extensions.Configuration;

namespace CaddyUi.Infrastructure.Operations;

public enum OperationsWriteMode
{
    Disabled,
    Shadow,
    Active,
}

public sealed class OperationsOptions
{
    public bool WorkerEnabled { get; init; }

    public OperationsWriteMode DnsWriteMode { get; init; } = OperationsWriteMode.Disabled;

    public int PollIntervalSeconds { get; init; } = 15;

    public int ProviderTimeoutSeconds { get; init; } = 10;

    public int HealthHistoryDays { get; init; } = 30;

    public string BackupDirectory { get; init; } = "/data/caddy-ui/backups";

    public string DiagnosticsDirectory { get; init; } = "/data/caddy-ui/diagnostics";

    public string CertificateDirectory { get; init; } = "/data/caddy/certificates";

    public string CaddyLogPath { get; init; } = "/var/log/caddy/caddy.log";

    public string ProviderSecretDirectory { get; init; } = "/run/caddy-ui-secrets";

    public string PgDumpBinary { get; init; } = "/usr/bin/pg_dump";

    public int BackupRetentionCount { get; init; } = 14;

    public IReadOnlyList<Uri> PublicIpv4Services { get; init; } =
    [
        new Uri("https://api.ipify.org"),
        new Uri("https://ipv4.icanhazip.com"),
    ];

    public IReadOnlyList<Uri> PublicIpv6Services { get; init; } =
    [
        new Uri("https://api64.ipify.org"),
        new Uri("https://ipv6.icanhazip.com"),
    ];

    public IReadOnlySet<string> InstalledCaddyDnsModules { get; init; } =
        new HashSet<string>(["netcup"], StringComparer.OrdinalIgnoreCase);

    public static OperationsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Operations");
        return new OperationsOptions
        {
            WorkerEnabled = ReadBool(configuration, section, "WorkerEnabled", "CADDY_UI_OPERATIONS_WORKER_ENABLED", false),
            DnsWriteMode = ParseWriteMode(Read(configuration, section, "DnsWriteMode", "CADDY_UI_DNS_WRITE_MODE", "disabled")),
            PollIntervalSeconds = Clamp(ReadInt(configuration, section, "PollIntervalSeconds", "CADDY_UI_OPERATIONS_POLL_SECONDS", 15), 2, 300),
            ProviderTimeoutSeconds = Clamp(ReadInt(configuration, section, "ProviderTimeoutSeconds", "CADDY_UI_PROVIDER_TIMEOUT_SECONDS", 10), 2, 120),
            HealthHistoryDays = Clamp(ReadInt(configuration, section, "HealthHistoryDays", "CADDY_UI_HEALTH_HISTORY_DAYS", 30), 1, 365),
            BackupDirectory = Read(configuration, section, "BackupDirectory", "CADDY_UI_BACKUP_DIRECTORY", "/data/caddy-ui/backups"),
            DiagnosticsDirectory = Read(configuration, section, "DiagnosticsDirectory", "CADDY_UI_DIAGNOSTICS_DIRECTORY", "/data/caddy-ui/diagnostics"),
            CertificateDirectory = Read(configuration, section, "CertificateDirectory", "CADDY_UI_CERTIFICATE_DIRECTORY", "/data/caddy/certificates"),
            CaddyLogPath = Read(configuration, section, "CaddyLogPath", "CADDY_UI_CADDY_LOG_PATH", "/var/log/caddy/caddy.log"),
            ProviderSecretDirectory = Read(configuration, section, "ProviderSecretDirectory", "CADDY_UI_PROVIDER_SECRET_DIRECTORY", "/run/caddy-ui-secrets"),
            PgDumpBinary = Read(configuration, section, "PgDumpBinary", "CADDY_UI_PG_DUMP_BINARY", "/usr/bin/pg_dump"),
            BackupRetentionCount = Clamp(ReadInt(configuration, section, "BackupRetentionCount", "CADDY_UI_BACKUP_RETENTION_COUNT", 14), 1, 365),
            PublicIpv4Services = ReadUris(section.GetSection("PublicIpv4Services"), [new Uri("https://api.ipify.org"), new Uri("https://ipv4.icanhazip.com")]),
            PublicIpv6Services = ReadUris(section.GetSection("PublicIpv6Services"), [new Uri("https://api64.ipify.org"), new Uri("https://ipv6.icanhazip.com")]),
            InstalledCaddyDnsModules = ReadModules(configuration, section),
        };
    }

    private static IReadOnlySet<string> ReadModules(IConfiguration configuration, IConfiguration section)
    {
        var raw = Environment.GetEnvironmentVariable("CADDY_UI_CADDY_DNS_MODULES") ?? section["InstalledCaddyDnsModules"] ?? "netcup";
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<Uri> ReadUris(IConfigurationSection section, IReadOnlyList<Uri> fallback)
    {
        var values = section.Get<string[]>() ?? [];
        var result = values
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null)
            .Where(uri => uri is not null)
            .Cast<Uri>()
            .ToArray();
        return result.Length == 0 ? fallback : result;
    }

    private static OperationsWriteMode ParseWriteMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "shadow" => OperationsWriteMode.Shadow,
            "active" => OperationsWriteMode.Active,
            _ => OperationsWriteMode.Disabled,
        };
    }

    private static bool ReadBool(IConfiguration configuration, IConfiguration section, string key, string environment, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(environment) ?? section[key];
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int ReadInt(IConfiguration configuration, IConfiguration section, string key, string environment, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(environment) ?? section[key];
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static string Read(IConfiguration configuration, IConfiguration section, string key, string environment, string fallback)
    {
        return (Environment.GetEnvironmentVariable(environment) ?? section[key] ?? fallback).Trim();
    }

    private static int Clamp(int value, int minimum, int maximum) => Math.Clamp(value, minimum, maximum);
}
