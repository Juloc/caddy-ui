using CaddyUi.Application.Dns;
using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Tests;

[Collection(CaddyCertificateSourceRegistryCollection.Name)]
public sealed class DnsChallengeTimingTests : IDisposable
{
    public DnsChallengeTimingTests()
    {
        CaddyCertificateSourceRegistry.Clear();
    }

    public void Dispose()
    {
        CaddyCertificateSourceRegistry.Clear();
    }

    [Theory]
    [InlineData("600", "600s")]
    [InlineData("10m", "10m")]
    [InlineData("1h", "1h")]
    public void NormalizeDelay_AcceptsSupportedDurations(string value, string expected)
    {
        Assert.Equal(expected, DnsChallengeTiming.NormalizeDelay(value));
    }

    [Fact]
    public void NormalizeTimeout_RejectsZero()
    {
        Assert.Throws<ArgumentException>(() => DnsChallengeTiming.NormalizeTimeout("0s"));
    }

    [Fact]
    public void Compile_RendersConfiguredDnsPropagationTiming()
    {
        var provider = NetcupProvider(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["customer_number"] = "123456",
            [DnsChallengeTiming.PropagationDelayKey] = "600s",
            [DnsChallengeTiming.PropagationTimeoutKey] = "900s",
        });
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        var compilation = compiler.Compile([
            new CaddyRouteSource(CreateProxy(), string.Empty, "wildcard", provider),
        ]);

        Assert.True(compilation.CertificateReadyForActiveApply);
        Assert.Contains("propagation_delay 600s", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("propagation_timeout 900s", compilation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_BlocksInvalidDnsPropagationTiming()
    {
        var provider = NetcupProvider(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["customer_number"] = "123456",
            [DnsChallengeTiming.PropagationDelayKey] = "600ubs",
        });
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        var compilation = compiler.Compile([
            new CaddyRouteSource(CreateProxy(), string.Empty, "wildcard", provider),
        ]);

        Assert.False(compilation.CertificateReadyForActiveApply);
        Assert.Contains(compilation.Warnings, warning =>
            warning.Contains("Propagation-Delay", StringComparison.Ordinal));
    }

    private static CaddyDnsProviderSource NetcupProvider(IReadOnlyDictionary<string, string> settings)
    {
        return new CaddyDnsProviderSource(
            "netcup",
            true,
            true,
            settings,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api_key"] = "secret://env/NETCUP_API_KEY",
                ["api_password"] = "NETCUP_API_PASSWORD",
            });
    }

    private static ManagedRouteDefinition CreateProxy()
    {
        return ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Mealie",
            Guid.NewGuid(),
            "example.com",
            "mealie",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Inherit,
            null,
            RouteConfigurationDocument.Empty with
            {
                PathPrefix = "/",
                Upstream = "mealie:9925",
            });
    }
}
