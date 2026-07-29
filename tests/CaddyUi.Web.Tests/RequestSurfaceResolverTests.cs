using System.Net;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace CaddyUi.Web.Tests;

public sealed class RequestSurfaceResolverTests
{
    [Fact]
    public void DirectPrivateAddress_IsLanSurface()
    {
        var resolver = CreateResolver();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        context.Connection.LocalPort = 8098;
        context.Request.Host = new HostString("192.168.1.26", 8098);
        context.Request.Scheme = "http";

        Assert.Equal(RequestSurface.Lan, resolver.Resolve(context));
    }

    [Fact]
    public void PublicSurface_RequiresExactOriginAndProxySecret()
    {
        var resolver = CreateResolver(
            ("CADDY_UI_PUBLIC_ORIGIN", "https://caddy.example.com"),
            ("CADDY_UI_ADMIN_PROXY_SECRET", "admin-secret"));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.20.0.2");
        context.Connection.LocalPort = 8098;
        context.Request.Host = new HostString("caddy-ui", 8098);
        context.Request.Headers[RequestSurfaceResolver.AdminProxyHeader] = "admin-secret";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "caddy.example.com";

        Assert.Equal(RequestSurface.PublicAdmin, resolver.Resolve(context));

        context.Request.Headers["X-Forwarded-Host"] = "other.example.com";
        Assert.Equal(RequestSurface.Rejected, resolver.Resolve(context));
    }

    [Fact]
    public void PortalSurface_IsSeparatedByPortAndSecret()
    {
        var resolver = CreateResolver(("CADDY_UI_PORTAL_PROXY_SECRET", "portal-secret"));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.20.0.2");
        context.Connection.LocalPort = 8099;
        context.Request.Headers[RequestSurfaceResolver.PortalProxyHeader] = "portal-secret";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "app.example.com";

        Assert.Equal(RequestSurface.Portal, resolver.Resolve(context));
    }

    private static RequestSurfaceResolver CreateResolver(
        params (string Key, string Value)[] settings)
    {
        var values = settings.ToDictionary(
            setting => setting.Key,
            setting => (string?)setting.Value,
            StringComparer.Ordinal);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new RequestSurfaceResolver(
            SecurityRuntimeOptions.FromConfiguration(configuration));
    }
}
