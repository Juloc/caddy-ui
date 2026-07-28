namespace CaddyUi.Infrastructure.Management;

public sealed record DnsProviderRecord(
    Guid Id,
    string ProviderType,
    string Label,
    bool Enabled,
    string ConfigJson,
    string SecretReferencesJson,
    DateTimeOffset? LastTestedAt,
    string LastTestStatus,
    string LastTestError);

public sealed record ManagedDomainRecord(
    Guid Id,
    string Name,
    string DisplayName,
    bool Enabled,
    bool IsDefault,
    string DefaultCertificateMode,
    Guid? DnsProviderId,
    string? DnsProviderLabel,
    int RouteCount);
