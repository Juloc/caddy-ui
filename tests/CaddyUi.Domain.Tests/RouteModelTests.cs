using CaddyUi.Domain.Routing;

namespace CaddyUi.Domain.Tests;

public sealed class RouteModelTests
{
    [Fact]
    public void Create_ComposesHostAndNormalizesProxyConfiguration()
    {
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            " Mealie ",
            Guid.NewGuid(),
            "Example.COM.",
            " Meals ",
            ManagedRouteKind.Proxy,
            true,
            5,
            RouteCertificateMode.Inherit,
            null,
            RouteConfigurationDocument.Empty with
            {
                PathPrefix = "/recipes/",
                Upstream = "MEALIE:9925",
                HealthPath = "/health/",
            });

        Assert.Equal("Mealie", route.Name);
        Assert.Equal("meals.example.com", route.Host);
        Assert.Equal("/recipes", route.Configuration.PathPrefix);
        Assert.Equal("mealie:9925", route.Configuration.Upstream);
        Assert.Equal("/health", route.Configuration.HealthPath);
    }

    [Theory]
    [InlineData("*", "*.example.com")]
    [InlineData("*.", "*.example.com")]
    [InlineData("*.OS", "*.os.example.com")]
    [InlineData("*.internal.apps", "*.internal.apps.example.com")]
    public void Create_AllowsLeadingWildcardSubdomain(string subdomain, string expectedHost)
    {
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Wildcard",
            Guid.NewGuid(),
            "example.com",
            subdomain,
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Inherit,
            null,
            RouteConfigurationDocument.Empty with { Upstream = "app:8080" });

        Assert.Equal(expectedHost, route.Host);
    }

    [Theory]
    [InlineData("foo.*")]
    [InlineData("*foo")]
    [InlineData("*.foo.*")]
    [InlineData("**")]
    public void Create_RejectsWildcardOutsideLeadingLabel(string subdomain)
    {
        Assert.Throws<ArgumentException>(() => ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Wildcard",
            Guid.NewGuid(),
            "example.com",
            subdomain,
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Inherit,
            null,
            RouteConfigurationDocument.Empty with { Upstream = "app:8080" }));
    }

    [Theory]
    [InlineData("https://example.com/{danger}")]
    [InlineData("example.com:8080\nrespond 200")]
    public void Create_RejectsGeneratedConfigurationInjection(string upstream)
    {
        Assert.Throws<ArgumentException>(() => ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Unsafe",
            Guid.NewGuid(),
            "example.com",
            "unsafe",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with { Upstream = upstream }));
    }

    [Fact]
    public void Create_RequiresContentForCustomRoute()
    {
        Assert.Throws<ArgumentException>(() => ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Custom",
            Guid.NewGuid(),
            "example.com",
            "custom",
            ManagedRouteKind.Custom,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty));
    }
}
