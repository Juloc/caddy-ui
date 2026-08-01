using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Tests;

public sealed class PublicAdminRouteCompilerTests
{
    [Fact]
    public void Compile_PublicAdminHost_AddsTheAdminProxyContractOnlyToThatHost()
    {
        var publicAdmin = CreateRoute("caddy", "caddy.example.com", "caddy-ui:8098");
        var ordinaryApp = CreateRoute("app", "app.example.com", "app:8080");
        var compiler = new CaddyRouteCompiler(
            false,
            "caddy-ui:8099",
            "https://caddy.example.com");

        var adminCompilation = compiler.Compile([
            new CaddyRouteSource(publicAdmin, string.Empty),
        ]);
        var ordinaryCompilation = compiler.Compile([
            new CaddyRouteSource(ordinaryApp, string.Empty),
        ]);

        Assert.Contains(
            "header_up X-Caddy-Admin-Secret {env.CADDY_UI_ADMIN_PROXY_SECRET}",
            adminCompilation.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "header_up X-Forwarded-Proto https",
            adminCompilation.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "header_up X-Forwarded-Host {host}",
            adminCompilation.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "X-Caddy-Admin-Secret",
            ordinaryCompilation.Content,
            StringComparison.Ordinal);
    }

    private static ManagedRouteDefinition CreateRoute(
        string name,
        string host,
        string upstream)
    {
        var separator = host.IndexOf('.', StringComparison.Ordinal);
        var subdomain = separator > 0 ? host[..separator] : string.Empty;
        var domain = separator > 0 ? host[(separator + 1)..] : host;
        return ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            name,
            Guid.NewGuid(),
            domain,
            subdomain,
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with
            {
                Upstream = upstream,
            });
    }
}
