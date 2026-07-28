using CaddyUi.Application.Dns;

namespace CaddyUi.Application.Tests;

public sealed class DnsProviderCatalogTests
{
    [Fact]
    public void Catalog_ContainsCommonProvidersWithUniqueTypes()
    {
        var types = DnsProviderCatalog.All.Select(provider => provider.Type).ToArray();

        Assert.Equal(types.Length, types.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("netcup", types);
        Assert.Contains("cloudflare", types);
        Assert.Contains("route53", types);
        Assert.Contains("hetzner", types);
        Assert.Contains("ionos", types);
        Assert.Contains("rfc2136", types);
        Assert.All(
            DnsProviderCatalog.All,
            provider => Assert.True(
                provider.Capabilities.HasFlag(DnsProviderCapability.DnsChallenge),
                $"Provider {provider.Type} must support DNS-01 for wildcard certificates."));
    }
}
