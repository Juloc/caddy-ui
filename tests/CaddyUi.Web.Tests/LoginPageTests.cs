using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaddyUi.Web.Tests;

public sealed class LoginPageTests :
    IClassFixture<WebApplicationFactory<Program>>,
    IDisposable
{
    private readonly HttpClient _client;

    public LoginPageTests(WebApplicationFactory<Program> factory)
    {
        _client = factory
            .WithWebHostBuilder(builder =>
                builder.UseSetting(
                    "DataProtection:PersistKeysToPostgreSql",
                    "false"))
            .CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false,
                });
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    [Fact]
    public async Task LoginPage_RendersWithAnIsolatedTestKeyRing()
    {
        using var response = await _client.GetAsync("/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Overview_RedirectsToLogin()
    {
        using var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }
}
