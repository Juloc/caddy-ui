using CaddyUi.Domain.Routing;

namespace CaddyUi.Domain.Tests;

public sealed class StaticSiteRouteTests
{
    [Fact]
    public void StaticSite_NormalizesAndRoundTripsStorageKind()
    {
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Mini-site",
            Guid.NewGuid(),
            "Example.COM",
            "Paperless-Backup",
            ManagedRouteKind.StaticSite,
            true,
            0,
            RouteCertificateMode.Inherit,
            null,
            RouteConfigurationDocument.Empty with
            {
                StaticSiteTitle = " Paperless Backup ",
                StaticSiteContact = " owner@example.com ",
                StaticSiteHomeText = " Home ",
                StaticSitePrivacyText = " Privacy ",
                StaticSiteTermsText = " Terms ",
            });

        Assert.Equal("paperless-backup.example.com", route.Host);
        Assert.Equal("Paperless Backup", route.Configuration.StaticSiteTitle);
        Assert.Equal("owner@example.com", route.Configuration.StaticSiteContact);
        Assert.Equal("static_site", ManagedRouteDefinition.ToStorageValue(route.Kind));
        Assert.Equal(ManagedRouteKind.StaticSite, ManagedRouteDefinition.ParseKind("static_site"));
    }

    [Fact]
    public void StaticSite_RequiresRootPathAndPublicAccess()
    {
        var configuration = RouteConfigurationDocument.Empty with
        {
            PathPrefix = "/private",
            StaticSiteTitle = "Site",
            StaticSiteHomeText = "Home",
            StaticSitePrivacyText = "Privacy",
            StaticSiteTermsText = "Terms",
        };

        Assert.Throws<ArgumentException>(() => ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Mini-site",
            Guid.NewGuid(),
            "example.com",
            "site",
            ManagedRouteKind.StaticSite,
            true,
            0,
            RouteCertificateMode.Inherit,
            null,
            configuration));

        Assert.Throws<ArgumentException>(() => ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Mini-site",
            Guid.NewGuid(),
            "example.com",
            "site",
            ManagedRouteKind.StaticSite,
            true,
            0,
            RouteCertificateMode.Inherit,
            Guid.NewGuid(),
            configuration with { PathPrefix = "/" }));
    }
}
