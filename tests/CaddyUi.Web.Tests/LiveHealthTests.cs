using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaddyUi.Web.Tests;

public sealed class LiveHealthTests :
    IClassFixture<WebApplicationFactory<Program>>,
    IDisposable
{
    private readonly HttpClient _client;

    public LiveHealthTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    [Fact]
    public async Task LiveHealth_DoesNotRequirePostgreSql()
    {
        using var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
