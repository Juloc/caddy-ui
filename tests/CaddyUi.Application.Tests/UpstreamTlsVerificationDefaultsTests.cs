using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Tests;

public sealed class UpstreamTlsVerificationDefaultsTests
{
    [Fact]
    public void Compile_VerifiesHttpsUpstreamCertificatesByDefault()
    {
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Secure service",
            Guid.NewGuid(),
            "example.com",
            "secure",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with
            {
                Upstream = "https://internal.example.com:443",
            });
        var compiler = new CaddyRouteCompiler(false, "caddy-ui:8099");

        var compilation = compiler.Compile([new CaddyRouteSource(route, string.Empty)]);

        Assert.DoesNotContain("tls_insecure_skip_verify", compilation.Content, StringComparison.Ordinal);
    }
}
