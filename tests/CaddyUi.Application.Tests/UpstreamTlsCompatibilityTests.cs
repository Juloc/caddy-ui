using System.Text.Json;
using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Tests;

public sealed class UpstreamTlsCompatibilityTests
{
    [Fact]
    public void Compile_RendersExplicitSelfSignedHttpsTransport()
    {
        var route = CreateProxy(
            "https://192.168.1.10:8006",
            skipUpstreamTlsVerification: true);
        var compiler = new CaddyRouteCompiler(false, "caddy-ui:8099");

        var compilation = compiler.Compile([new CaddyRouteSource(route, string.Empty)]);

        Assert.Contains("reverse_proxy \"https://192.168.1.10:8006\"", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("transport http {", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("tls_insecure_skip_verify", compilation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsTlsBypassForPlainHttpUpstream()
    {
        Assert.Throws<ArgumentException>(() => CreateProxy(
            "ae01-main:8080",
            skipUpstreamTlsVerification: true));
    }

    [Fact]
    public void LegacyConfiguration_DefaultsTlsBypassToDisabled()
    {
        const string json = """
            {
              "schema": "route-v1",
              "pathPrefix": "/",
              "upstream": "ae01-main:8080",
              "preserveHost": false,
              "healthPath": "",
              "healthIntervalSeconds": 30,
              "redirectTarget": "",
              "redirectPermanent": true,
              "staticStatusCode": 200,
              "staticBody": "",
              "customSnippet": ""
            }
            """;

        var configuration = JsonSerializer.Deserialize<RouteConfigurationDocument>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(configuration);
        Assert.False(configuration.SkipUpstreamTlsVerification);
    }

    private static ManagedRouteDefinition CreateProxy(
        string upstream,
        bool skipUpstreamTlsVerification)
    {
        return ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Service",
            Guid.NewGuid(),
            "example.com",
            "service",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with
            {
                Upstream = upstream,
                PreserveHost = true,
                SkipUpstreamTlsVerification = skipUpstreamTlsVerification,
            });
    }
}
