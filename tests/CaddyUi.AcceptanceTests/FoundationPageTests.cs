using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaddyUi.AcceptanceTests;

public sealed class FoundationPageTests :
    IClassFixture<WebApplicationFactory<Program>>,
    IDisposable
{
    private readonly HttpClient _client;

    public FoundationPageTests(WebApplicationFactory<Program> factory)
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
    public async Task Overview_RequiresAnAdminSession()
    {
        using var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_UsesRazorPhaseThreeLayout()
    {
        using var response = await _client.GetAsync("/Login");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Caddy UI", body, StringComparison.Ordinal);
        Assert.Contains("TOTP- oder Recovery-Code", body, StringComparison.Ordinal);
    }
}
