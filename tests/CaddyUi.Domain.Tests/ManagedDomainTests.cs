using CaddyUi.Domain.Certificates;
using CaddyUi.Domain.Domains;

namespace CaddyUi.Domain.Tests;

public sealed class ManagedDomainTests
{
    [Fact]
    public void Create_DefaultsToWildcardCertificates()
    {
        var domain = ManagedDomain.Create("Example.COM.");

        Assert.Equal("example.com", domain.Name);
        Assert.Equal("*.example.com", domain.WildcardHost);
        Assert.Equal(CertificateMode.Wildcard, domain.DefaultCertificateMode);
        Assert.Equal(
            CertificateMode.Wildcard,
            domain.ResolveCertificateMode(CertificateMode.Inherit));
    }

    [Theory]
    [InlineData("app.example.com", true)]
    [InlineData("api.example.com", true)]
    [InlineData("example.com", false)]
    [InlineData("deep.app.example.com", false)]
    [InlineData("app.other.example", false)]
    public void WildcardCovers_ExactlyOneSubdomainLevel(string host, bool expected)
    {
        var domain = ManagedDomain.Create("example.com");

        Assert.Equal(expected, domain.WildcardCovers(host));
    }

    [Fact]
    public void HostForSubdomain_AssignsRoutesToTheSelectedDomain()
    {
        var primary = ManagedDomain.Create("example.com");
        var secondary = ManagedDomain.Create("example.net");

        Assert.Equal("app.example.com", primary.HostForSubdomain("app"));
        Assert.Equal("app.example.net", secondary.HostForSubdomain("app"));
    }
}
