using CaddyUi.Application.Analytics;
using CaddyUi.Domain.Analytics;
using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AnalyticsIngestionStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_analytics")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_analytics")
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
    public async Task OneDocumentAndOneHundredAssets_ProduceOnePageView()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var parser = new CaddyAccessLogParser();
        var classifier = new RequestClassifier();
        var logs = new List<string>
        {
            CreateLog("/", "document", "text/html", 1785261600, 1024),
        };

        for (var index = 0; index < 100; index++)
        {
            logs.Add(
                CreateLog(
                    $"/_nuxt/chunk-{index:D3}.js",
                    "script",
                    "application/javascript",
                    1785261600.1 + (index * 0.01),
                    256));
        }

        var requests = logs
            .Select((json, index) => Parse(parser, classifier, json, index))
            .ToArray();
        var replay = logs
            .Select((json, index) => Parse(parser, classifier, json, index))
            .ToArray();

        var store = new AnalyticsIngestionStore(factory);
        var options = new AnalyticsIngestionOptions
        {
            Enabled = true,
            SessionIdleMinutes = 30,
            PageLoadWindowSeconds = 15,
        };
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

        var first = await store.PersistBatchAsync(
            "/logs/access.log",
            "identity-v1",
            10000,
            requests,
            Array.Empty<AnalyticsIngestionFailure>(),
            key,
            options);
        var second = await store.PersistBatchAsync(
            "/logs/access.log",
            "identity-v1",
            10000,
            replay,
            Array.Empty<AnalyticsIngestionFailure>(),
            key,
            options);

        Assert.Equal(101, first.RequestsInserted);
        Assert.Equal(1, first.PageViewsInserted);
        Assert.Equal(0, second.RequestsInserted);
        Assert.Equal(0, second.PageViewsInserted);

        await using var verification = factory.CreateDbContext();
        Assert.Equal(101, await CountAsync(verification, "caddy_ui.request_events"));
        Assert.Equal(1, await CountAsync(verification, "caddy_ui.navigation_events"));
        Assert.Equal(1, await CountAsync(verification, "caddy_ui.page_views"));
        Assert.Equal(1, await CountAsync(verification, "caddy_ui.analytics_sessions"));

        Assert.Equal(
            101,
            await verification.Database
                .SqlQueryRaw<long>(
                    "SELECT request_count AS \"Value\" FROM caddy_ui.analytics_sessions")
                .SingleAsync());
        Assert.Equal(
            1,
            await verification.Database
                .SqlQueryRaw<int>(
                    "SELECT page_view_count AS \"Value\" FROM caddy_ui.analytics_sessions")
                .SingleAsync());
        Assert.Equal(
            101,
            await verification.Database
                .SqlQueryRaw<int>(
                    "SELECT request_count AS \"Value\" FROM caddy_ui.page_loads")
                .SingleAsync());
        Assert.Equal(
            100,
            await verification.Database
                .SqlQueryRaw<int>(
                    "SELECT asset_request_count AS \"Value\" FROM caddy_ui.page_loads")
                .SingleAsync());
        Assert.Equal(
            0,
            await verification.Database
                .SqlQueryRaw<int>(
                    "SELECT api_request_count AS \"Value\" FROM caddy_ui.page_loads")
                .SingleAsync());
        Assert.Equal(
            101,
            await AggregateRequestCountAsync(
                verification,
                "caddy_ui.hourly_traffic_aggregates"));
        Assert.Equal(
            101,
            await AggregateRequestCountAsync(
                verification,
                "caddy_ui.daily_traffic_aggregates"));
        Assert.Equal(
            101,
            await AggregateRequestCountAsync(
                verification,
                "caddy_ui.monthly_traffic_aggregates"));
        Assert.Equal(
            1,
            await verification.Database
                .SqlQueryRaw<long>(
                    """
                    SELECT SUM(page_views)::bigint AS "Value"
                    FROM caddy_ui.hourly_traffic_aggregates
                    """)
                .SingleAsync());
        Assert.Equal(
            101,
            await verification.Database
                .SqlQueryRaw<long>(
                    """
                    SELECT SUM(request_count)::bigint AS "Value"
                    FROM caddy_ui.route_performance_aggregates
                    """)
                .SingleAsync());
    }

    private static async Task<long> CountAsync(
        CaddyUiDbContext database,
        string table)
    {
#pragma warning disable EF1002
        return await database.Database
            .SqlQueryRaw<long>($"SELECT COUNT(*) AS \"Value\" FROM {table}")
            .SingleAsync();
#pragma warning restore EF1002
    }

    private static async Task<long> AggregateRequestCountAsync(
        CaddyUiDbContext database,
        string table)
    {
#pragma warning disable EF1002
        return await database.Database
            .SqlQueryRaw<long>(
                $"SELECT SUM(requests)::bigint AS \"Value\" FROM {table}")
            .SingleAsync();
#pragma warning restore EF1002
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
