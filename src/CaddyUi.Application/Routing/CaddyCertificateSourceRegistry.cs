namespace CaddyUi.Application.Routing;

public sealed record CaddyDomainCertificateSource(
    Guid DomainId,
    string DomainName,
    string DomainCertificateMode,
    bool RequestWildcardCertificate,
    bool RequestBaseCertificate,
    CaddyDnsProviderSource? Provider);

public static class CaddyCertificateSourceRegistry
{
    private static IReadOnlyDictionary<Guid, CaddyDomainCertificateSource> _snapshot =
        new Dictionary<Guid, CaddyDomainCertificateSource>();

    public static CaddyDomainCertificateSource? Find(Guid domainId)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.TryGetValue(domainId, out var source) ? source : null;
    }

    public static IReadOnlyList<CaddyDomainCertificateSource> List()
    {
        return Volatile.Read(ref _snapshot).Values
            .OrderBy(source => source.DomainName, StringComparer.Ordinal)
            .ThenBy(source => source.DomainId)
            .ToArray();
    }

    public static void Replace(IEnumerable<CaddyDomainCertificateSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Volatile.Write(ref _snapshot, sources.ToDictionary(source => source.DomainId));
    }

    public static void Clear()
    {
        Volatile.Write(ref _snapshot, new Dictionary<Guid, CaddyDomainCertificateSource>());
    }
}
