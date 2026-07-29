namespace CaddyUi.Application.Routing;

public sealed record CaddyDomainCertificateSource(
    Guid DomainId,
    string DomainCertificateMode,
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
