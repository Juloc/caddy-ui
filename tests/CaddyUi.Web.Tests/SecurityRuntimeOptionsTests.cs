using CaddyUi.Web.Security;
using Microsoft.Extensions.Configuration;

namespace CaddyUi.Web.Tests;

public sealed class SecurityRuntimeOptionsTests
{
    [Fact]
    public void FromConfiguration_UsesDeploymentSpecificCookieNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Security:CookieNamespace"] = "caddy_ui_shadow",
                })
            .Build();

        var options = SecurityRuntimeOptions.FromConfiguration(configuration);

        Assert.Equal("caddy_ui_shadow_admin", options.LanAdminCookieName);
        Assert.Equal("__Host-caddy_ui_shadow_admin", options.PublicAdminCookieName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains.dot")]
    public void FromConfiguration_RejectsInvalidCookieNamespace(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Security:CookieNamespace"] = value,
                })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => SecurityRuntimeOptions.FromConfiguration(configuration));
    }
}
