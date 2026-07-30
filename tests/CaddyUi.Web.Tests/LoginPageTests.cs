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
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-valmsg-summary=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
        Assert.Contains("data-valmsg-for=\"Input.Username\"", html, StringComparison.Ordinal);
        Assert.Contains("data-valmsg-for=\"Input.Password\"", html, StringComparison.Ordinal);
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
    [InlineData("/Routing")]
    [InlineData("/Routing/Edit")]
    [InlineData("/Routing/Preview")]
    [InlineData("/Routing/Transfer")]
    [InlineData("/Routing/Transfer?handler=Download")]
    [InlineData("/Access")]
    [InlineData("/Administration/Providers")]
    [InlineData("/Operations/Dns")]
    [InlineData("/Operations/Jobs")]
    [InlineData("/Operations/Health")]
    [InlineData("/Operations/Notifications")]
    [InlineData("/Operations/Backup")]
    [InlineData("/Operations/Backup?handler=Diagnostics")]
    [InlineData("/Operations/Cutover")]
    public async Task ProtectedWorkspace_RedirectsAnonymousUsersToLogin(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }
}
