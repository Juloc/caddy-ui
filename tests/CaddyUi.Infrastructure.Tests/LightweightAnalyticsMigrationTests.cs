using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class LightweightAnalyticsMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task MigrationAndRollback_PreserveArchivedHistory()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        var previous = db.Database.GetMigrations().SkipLast(1).Last();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO caddy_ui.hourly_traffic_aggregates(bucket_start, host, status_class, actor_type, request_type,
                requests, page_views, bytes_sent, duration_sum_ms, duration_max_ms)
            VALUES ('2026-07-01T00:00:00Z', 'archive.example', '2xx', 'human', 'document', 42, 1, 1024, 42, 1)
            """);
        await migrator.MigrateAsync();
        Assert.Equal(42, await ArchivedRequestsAsync(db));
        await migrator.MigrateAsync(previous);
        Assert.Equal(42, await ArchivedRequestsAsync(db));
        await migrator.MigrateAsync();
        Assert.Equal(42, await ArchivedRequestsAsync(db));
    }

    [Fact]
    public async Task Retention_ExpiresNewDataAndKeepsExistingArchive()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO caddy_ui.traffic_buckets VALUES (CURRENT_TIMESTAMP, 'example.test', 'bot', 4, 10, 1024);
            INSERT INTO caddy_ui.security_events(occurred_at, kind, reason) VALUES
                ('2026-07-01T00:00:00Z', 'archive', 'preserve'), (CURRENT_TIMESTAMP, 'new', 'expire');
            """);
        var store = new AnalyticsIngestionStore(factory);
        await store.SaveRetentionAsync(90, 30);
        var read = new AnalyticsReadStore(factory, new(), new());
        Assert.Equal(90, (await read.GetSystemAsync()).StatisticsRetentionDays);
        await store.RunMaintenanceAsync(new(), now.AddDays(40));
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM caddy_ui.traffic_buckets").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM caddy_ui.security_events").SingleAsync());
        await store.SaveRetentionAsync(30, 30);
        await store.RunMaintenanceAsync(new(), now.AddDays(40));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM caddy_ui.traffic_buckets").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM caddy_ui.security_events").SingleAsync());
    }

    [Fact]
    public async Task Rollback_RefusesToDropCollectedData()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO caddy_ui.traffic_buckets VALUES (CURRENT_TIMESTAMP, 'example.test', 'bot', 4, 10, 1024)");
        var migrator = db.GetService<IMigrator>();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(db.Database.GetMigrations().SkipLast(1).Last()));
        Assert.Equal(10, await db.Database.SqlQueryRaw<long>("SELECT SUM(requests)::bigint AS \"Value\" FROM caddy_ui.traffic_buckets").SingleAsync());
    }

    private static Task<long> ArchivedRequestsAsync(CaddyUiDbContext db)
    {
        return db.Database.SqlQueryRaw<long>("SELECT SUM(requests)::bigint AS \"Value\" FROM caddy_ui.hourly_traffic_aggregates").SingleAsync();
    }
}
