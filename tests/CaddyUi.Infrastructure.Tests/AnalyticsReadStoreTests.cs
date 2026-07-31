using CaddyUi.Application.Analytics;
using CaddyUi.Domain.Analytics;
using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AnalyticsReadStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_read_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_read_tests")
        .Build();

    public Task InitializeAsync()
    {
        return _postgres.StartAsync();
    }

    public Task DisposeAsync()
    {
        return _postgres.DisposeAsync().AsTask();
    }

    [Fact]
    public void Filter_InvalidDimensionsAreRemovedAndWindowIsBounded()
    {
        var filter = AnalyticsReadFilter.Create(
            DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-28T12:00:00Z"),
            " MEALIE.EXAMPLE.COM ",
            "invalid",
            "asset",
            "9xx",
            5000);

        Assert.Equal("mealie.example.com", filter.Host);
        Assert.Equal(string.Empty, filter.ActorType);
        Assert.Equal("asset", filter.RequestType);
        Assert.Equal(string.Empty, filter.StatusClass);
        Assert.Equal(1000, filter.Limit);
        Assert.Equal(TimeSpan.FromDays(366), filter.To - filter.From);
    }

    [Fact]
    public async Task Dashboard_SeparatesOnePageViewFromAssetRequests()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var parser = new CaddyAccessLogParser();
        var classifier = new RequestClassifier();
        var logs = new[]
        {
            CreateLog("/", "document", "text/html", 1785261600, 1024),
            CreateLog("/_nuxt/app-0123456789abcdef.js", "script", "application/javascript", 1785261600.1, 512),
            CreateLog("/_nuxt/vendor-fedcba9876543210.js", "script", "application/javascript", 1785261600.2, 512),
        };
        var requests = logs
            .Select((json, index) => Parse(parser, classifier, json, index))
            .ToArray();
        var ingestion = new AnalyticsIngestionStore(factory);
        await ingestion.PersistBatchAsync(
            "/logs/access.log",
            "identity-v1",
            2048,
            requests,
            Array.Empty<AnalyticsIngestionFailure>(),
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
            new AnalyticsIngestionOptions
            {
                Enabled = true,
                SessionIdleMinutes = 30,
                PageLoadWindowSeconds = 15,
            });

        var readStore = new AnalyticsReadStore(
            factory,
            new AnalyticsIngestionOptions(),
            new IpSecurityOptions());
        var dashboard = await readStore.GetDashboardAsync(
            AnalyticsReadFilter.Create(
                DateTimeOffset.FromUnixTimeSeconds(1785261500),
                DateTimeOffset.FromUnixTimeSeconds(1785261700),
                host: "mealie.example.com"));

        var requestsWithoutCursor = await readStore.GetRequestsAsync(
            AnalyticsReadFilter.Create(
                DateTimeOffset.FromUnixTimeSeconds(1785261500),
                DateTimeOffset.FromUnixTimeSeconds(1785261700),
                host: "mealie.example.com"));

        Assert.Equal(3, requestsWithoutCursor.Count);

        Assert.Equal(3, dashboard.Requests);
        Assert.Equal(1, dashboard.PageViews);
        Assert.Equal(3, dashboard.RequestsPerPageView);
        Assert.Single(dashboard.TopPages);
        Assert.Equal("/", dashboard.TopPages[0].Path);
        Assert.Contains(
            dashboard.LargestAssets,
            item => item.Path == "/_nuxt/{token}.js" || item.Path.StartsWith("/_nuxt/", StringComparison.Ordinal));
    }

    private static ClassifiedRequest Parse(
        CaddyAccessLogParser parser,
        RequestClassifier classifier,
        string json,
        long offset)
    {
        Assert.True(
            parser.TryParse(
                json,
                "/logs/access.log",
                offset,
                out var request,
                out var error),
            error);
        return classifier.Classify(Assert.IsType<NormalizedRequestEvent>(request));
    }

    private static string CreateLog(
        string path,
        string destination,
        string contentType,
        double timestamp,
        int size)
    {
        return $$"""
            {
              "ts": {{timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
              "request": {
                "client_ip": "203.0.113.10",
                "method": "GET",
                "host": "mealie.example.com",
                "uri": "{{path}}",
                "headers": {
                  "User-Agent": ["Mozilla/5.0 Chrome/140.0"],
                  "Accept": ["{{(destination == "document" ? "text/html" : "*/*")}}"],
                  "Sec-Fetch-Dest": ["{{destination}}"]
                }
              },
              "duration": 0.005,
              "size": {{size}},
              "status": 200,
              "resp_headers": {
                "Content-Type": ["{{contentType}}"]
              }
            }
            """;
    }
}
