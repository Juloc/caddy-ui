using CaddyUi.Application.Analytics;
using CaddyUi.Application.Security;
using CaddyUi.Domain.Analytics;
using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class ClientRiskAssessmentStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_risk_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_risk_tests")
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
    public async Task Assessment_IsStoredOncePerRefreshWindowWithReasons()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
        var parser = new CaddyAccessLogParser();
        var classifier = new RequestClassifier();
        var requests = Enumerable.Range(0, 30)
            .Select(index => Parse(
                parser,
                classifier,
                CreateLog(
                    now.AddSeconds(-30 + index),
                    index % 2 == 0 ? "/.env" : $"/missing-{index}"),
                index))
            .ToArray();
        var analyticsStore = new AnalyticsIngestionStore(factory);
        await analyticsStore.PersistBatchAsync(
            "/logs/risk.log",
            "risk-log-v1",
            3000,
            requests,
            Array.Empty<AnalyticsIngestionFailure>(),
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
            new AnalyticsIngestionOptions
            {
                Enabled = true,
                SessionIdleMinutes = 30,
                PageLoadWindowSeconds = 15,
            },
            CancellationToken.None);

        var riskStore = new ClientRiskAssessmentStore(
            factory,
            new ClientRiskEngine(),
            new IpSecurityOptions
            {
                RiskAssessmentEnabled = true,
                RiskWindowMinutes = 30,
                RiskRefreshMinutes = 5,
                RiskBatchSize = 100,
            },
            new FixedTimeProvider(now));

        var firstCount = await riskStore.AssessReadyClientsAsync(CancellationToken.None);
        var secondCount = await riskStore.AssessReadyClientsAsync(CancellationToken.None);

        Assert.Equal(1, firstCount);
        Assert.Equal(0, secondCount);

        await using var verification = factory.CreateDbContext();
        Assert.Equal(
            1,
            await verification.Database
                .SqlQueryRaw<long>(
                    "SELECT COUNT(*) AS \"Value\" FROM caddy_ui.client_assessments")
                .SingleAsync());
        Assert.True(
            await verification.Database
                .SqlQueryRaw<long>(
                    "SELECT COUNT(*) AS \"Value\" FROM caddy_ui.client_assessment_reasons")
                .SingleAsync() > 0);
        Assert.Equal(
            1,
            await verification.Database
                .SqlQueryRaw<long>(
                    "SELECT COUNT(*) AS \"Value\" FROM caddy_ui.security_events WHERE kind = 'client-risk-assessment'")
                .SingleAsync());
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
                "/logs/risk.log",
                offset,
                out var request,
                out var error),
            error);
        return classifier.Classify(Assert.IsType<NormalizedRequestEvent>(request));
    }

    private static string CreateLog(DateTimeOffset occurredAt, string path)
    {
        return $$"""
            {
              "ts": {{occurredAt.ToUnixTimeMilliseconds() / 1000.0}},
              "request": {
                "client_ip": "203.0.113.10",
                "method": "GET",
                "host": "example.test",
                "uri": "{{path}}",
                "headers": {
                  "User-Agent": ["ExampleScannerBot/1.0"],
                  "Accept": ["text/html"]
                }
              },
              "duration": 0.002,
              "size": 128,
              "status": 404,
              "resp_headers": {
                "Content-Type": ["text/html"]
              }
            }
            """;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }
}
