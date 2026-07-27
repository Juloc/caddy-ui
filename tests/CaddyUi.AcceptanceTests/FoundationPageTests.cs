using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaddyUi.AcceptanceTests;

public sealed class FoundationPageTests :
    IClassFixture<WebApplicationFactory<Program>>,
    IDisposable
{
    private readonly HttpClient _client;

    public FoundationPageTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    [Fact]
    public async Task Overview_UsesRazorFoundationLayout()
    {
        using var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Caddy UI 2.0", body, StringComparison.Ordinal);
        Assert.Contains("Parallelbetrieb", body, StringComparison.Ordinal);
    }
}
