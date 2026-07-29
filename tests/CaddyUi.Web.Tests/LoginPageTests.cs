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

    [Theory]
    [InlineData("/")]
    [InlineData("/Traffic")]
    [InlineData("/Requests")]
    [InlineData("/Routes/Analytics")]
    [InlineData("/Security/Overview")]
    [InlineData("/Performance")]
    [InlineData("/LiveLog")]
    [InlineData("/System")]
    [InlineData("/events/live?once=true")]
    public async Task ReadOnlyWorkspace_RedirectsAnonymousUsersToLogin(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }
}
