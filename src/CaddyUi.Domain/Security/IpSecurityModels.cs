using System.Net;

namespace CaddyUi.Domain.Security;

public enum IpAddressScope
{
    Public,
    Private,
    Loopback,
    LinkLocal,
    Multicast,
    Documentation,
    Shared,
    Benchmark,
    Reserved,
    Unspecified,
}

public sealed record IpAddressClassification(
    IPAddress Address,
    string NormalizedAddress,
    IpAddressScope Scope,
    bool ExternalLookupAllowed);

public sealed record IpIntelligenceResult(
    IPAddress Address,
    IpAddressScope Scope,
    bool Available,
    string Asn,
    string Prefix,
    string Holder,
    string Registry,
    string Source,
    DateTimeOffset FetchedAt,
    DateTimeOffset ExpiresAt,
    string Error,
    string PayloadJson);

public enum ClientRiskLevel
{
    Low,
    Medium,
    High,
    Unknown,
}

public sealed record ClientRiskReason(
    string Code,
    string Message,
    int Weight,
    IReadOnlyDictionary<string, string> Evidence);

public sealed record ClientRiskAssessment(
    string Classification,
    int AutomationScore,
    ClientRiskLevel RiskLevel,
    string EngineVersion,
    DateTimeOffset SampleStartedAt,
    DateTimeOffset SampleEndedAt,
    IReadOnlyList<ClientRiskReason> Reasons);

public sealed record ClientRiskSample(
    string ExistingActorType,
    string UserAgent,
    long RequestCount,
    TimeSpan Window,
    double IntervalRegularity,
    int DistinctPathCount,
    int ScannerPathCount,
    double NotFoundRatio,
    double AuthenticationFailureRatio,
    int UnsafeMethodCount,
    int HostCount,
    bool KnownBotSignature,
    DateTimeOffset SampleStartedAt,
    DateTimeOffset SampleEndedAt);

public static class IpSecurityStorageValues
{
    public static string ToStorageValue(this IpAddressScope value)
    {
        return value switch
        {
            IpAddressScope.Public => "public",
            IpAddressScope.Private => "private",
            IpAddressScope.Loopback => "loopback",
            IpAddressScope.LinkLocal => "link-local",
            IpAddressScope.Multicast => "multicast",
            IpAddressScope.Documentation => "documentation",
            IpAddressScope.Shared => "shared",
            IpAddressScope.Benchmark => "benchmark",
            IpAddressScope.Reserved => "reserved",
            IpAddressScope.Unspecified => "unspecified",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    public static string ToStorageValue(this ClientRiskLevel value)
    {
        return value switch
        {
            ClientRiskLevel.Low => "low",
            ClientRiskLevel.Medium => "medium",
            ClientRiskLevel.High => "high",
            ClientRiskLevel.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }
}
