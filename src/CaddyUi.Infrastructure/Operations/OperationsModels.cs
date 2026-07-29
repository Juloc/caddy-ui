using System.Text.Json;

namespace CaddyUi.Infrastructure.Operations;

public sealed record DnsProviderRuntimeRecord(
    Guid Id,
    string ProviderType,
    string Label,
    bool Enabled,
    string ConfigJson,
    string SecretReferencesJson,
    DateTimeOffset? LastTestedAt,
    string LastTestStatus,
    string LastTestError);

public sealed record ManagedDnsRecord(
    Guid Id,
    Guid DomainId,
    string DomainName,
    Guid ProviderId,
    string ProviderLabel,
    string Name,
    string RecordType,
    string Value,
    int Ttl,
    int? Priority,
    bool Enabled,
    string Source,
    DateTimeOffset? LastSyncAt,
    string LastSyncStatus,
    string LastSyncError,
    DateTimeOffset UpdatedAt)
{
    public string Fqdn => Name is "" or "@" ? DomainName : $"{Name}.{DomainName}";
}

public sealed record DdnsTargetRecord(
    Guid Id,
    Guid DomainId,
    string DomainName,
    Guid ProviderId,
    string ProviderLabel,
    string Name,
    string RecordType,
    bool Enabled,
    int IntervalSeconds,
    string AddressSource,
    string StaticValue,
    string LastValue,
    DateTimeOffset NextRunAt,
    DateTimeOffset? LastRunAt,
    string LastStatus,
    string LastError,
    DateTimeOffset UpdatedAt)
{
    public string Fqdn => Name is "" or "@" ? DomainName : $"{Name}.{DomainName}";
}

public sealed record NotificationChannelRecord(
    Guid Id,
    string Name,
    string ChannelType,
    bool Enabled,
    string ConfigJson,
    string SecretReferencesJson,
    DateTimeOffset? LastTestedAt,
    string LastTestStatus,
    string LastTestError,
    DateTimeOffset UpdatedAt);

public sealed record ScheduledJobRecord(
    Guid Id,
    string Name,
    string JobType,
    bool Enabled,
    int IntervalSeconds,
    string ConfigJson,
    DateTimeOffset NextRunAt,
    DateTimeOffset? LastRunAt,
    string LastStatus,
    string LastError,
    DateTimeOffset UpdatedAt);

public sealed record JobRunRecord(
    Guid Id,
    Guid JobId,
    string JobName,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string Message,
    string DetailsJson,
    string CorrelationId);

public sealed record HealthTargetRecord(
    Guid Id,
    string Name,
    string TargetType,
    string Url,
    bool Enabled,
    int ExpectedStatusMin,
    int ExpectedStatusMax,
    int TimeoutSeconds,
    DateTimeOffset? LastCheckedAt,
    string LastStatus,
    int? LastHttpStatus,
    double? LastDurationMilliseconds,
    string LastError,
    DateTimeOffset UpdatedAt);

public sealed record BackupArtifactRecord(
    Guid Id,
    DateTimeOffset CreatedAt,
    string FileName,
    string Path,
    long SizeBytes,
    string Digest,
    string Status,
    string Error,
    string ManifestJson);

public sealed record DnsProviderContext(
    DnsProviderRuntimeRecord Provider,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyDictionary<string, string> Secrets);

public sealed record DnsRecordMutation(
    string Domain,
    string Name,
    string RecordType,
    string Value,
    int Ttl,
    int? Priority = null)
{
    public string Fqdn => Name is "" or "@" ? Domain : $"{Name}.{Domain}";
}

public sealed record ProviderOperationResult(bool Succeeded, string Message, string ExternalId = "")
{
    public static ProviderOperationResult Success(string message, string externalId = "") => new(true, message, externalId);

    public static ProviderOperationResult Failure(string message) => new(false, message);
}

public sealed record SystemNotification(
    string Severity,
    string EventType,
    string Title,
    string Message,
    string ObjectType = "",
    string ObjectId = "");

public static class OperationsJson
{
    public static IReadOnlyDictionary<string, string> ReadStringObject(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Configuration must be a JSON object.");
        }

        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText(),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string Required(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"Missing required provider setting '{key}'.");
    }

    public static string Optional(IReadOnlyDictionary<string, string> values, string key, string fallback = "")
    {
        return values.TryGetValue(key, out var value) ? value.Trim() : fallback;
    }
}
