using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Tests;

public sealed class PortalForwardAuthCompilerTests
{
    [Fact]
    public void Compile_ProtectedRoute_RendersCompletePortalContractBeforeApplicationRoute()
    {
        var accessGroupId = Guid.NewGuid();
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Protected app",
            Guid.NewGuid(),
            "example.com",
            "app",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            accessGroupId,
            RouteConfigurationDocument.Empty with
            {
                Upstream = "app:8080",
            });
        var compiler = new CaddyRouteCompiler(false, "caddy-ui:8099");

        var compilation = compiler.Compile([
            new CaddyRouteSource(route, "Family"),
        ]);

        Assert.Contains(
            "handle /__caddy_ui_auth/* {\n        reverse_proxy caddy-ui:8099 {",
            compilation.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "header_up X-Caddy-Portal-Secret {env.CADDY_UI_PORTAL_PROXY_SECRET}",
            compilation.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "header_up X-Forwarded-Proto https",
            compilation.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "header_up X-Forwarded-Host {host}",
            compilation.Content,
            StringComparison.Ordinal);

        var portalHandleIndex = compilation.Content.IndexOf(
            "handle /__caddy_ui_auth/*",
            StringComparison.Ordinal);
        var applicationRouteIndex = compilation.Content.IndexOf(
            "# Protected app",
            StringComparison.Ordinal);
        Assert.True(portalHandleIndex >= 0);
        Assert.True(applicationRouteIndex > portalHandleIndex);
    }
}
