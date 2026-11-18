using System.Diagnostics;
using CaddyUi.Application.Analytics;
using CaddyUi.Infrastructure.Analytics;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class AnalyticsIngestionWorkerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"caddy-ui-ingestion-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await _postgres.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Worker_IdlePollingAvoidsDatabaseAndRecoversAfterFailureAndRestart()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var context = factory.CreateDbContext())
        {
            await context.Database.MigrateAsync();
        }

        var counted = new CountingContextFactory(factory);
        var store = new AnalyticsIngestionStore(counted);
        using var keyProvider = new AnalyticsClientKeyProvider(factory, new EphemeralDataProtectionProvider());
        var path = Path.Combine(_directory, "access.log");
        var options = new AnalyticsIngestionOptions { Enabled = true, LogPaths = new[] { path }, PollIntervalMilliseconds = 10, BatchSize = 10 };
        var metrics = new AnalyticsIngestionRuntimeMetrics();

        AnalyticsIngestionWorker CreateWorker() => new(options, new AnalyticsLogTailer(), new CaddyAccessLogParser(), new RequestClassifier(), store, metrics, keyProvider, NullLogger<AnalyticsIngestionWorker>.Instance);

        using (var worker = CreateWorker())
        {
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await Task.Delay(150);
                Assert.Equal(0, counted.Count);
                await File.WriteAllTextAsync(path, "not-json\n");
                await WaitUntilAsync(() => metrics.GetLatest()?.CheckpointOffset == new FileInfo(path).Length);
                Assert.Equal(2, counted.Count);
                await Task.Delay(150);
                Assert.Equal(2, counted.Count);

                counted.FailNext = true;
                await File.AppendAllTextAsync(path, "also-not-json\n");
                await WaitUntilAsync(() => metrics.GetLatest()?.CheckpointOffset == new FileInfo(path).Length);
                Assert.Equal(5, counted.Count);
                await Task.Delay(150);
                Assert.Equal(5, counted.Count);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }

        using (var restarted = CreateWorker())
        {
            await restarted.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => counted.Count == 6);
                await Task.Delay(150);
                Assert.Equal(6, counted.Count);
                await File.AppendAllTextAsync(path, "last-not-json\n");
                await WaitUntilAsync(() => metrics.GetLatest()?.CheckpointOffset == new FileInfo(path).Length);
                Assert.Equal(7, counted.Count);
            }
            finally
            {
                await restarted.StopAsync(CancellationToken.None);
            }
        }

        await using var verification = factory.CreateDbContext();
        Assert.Equal(3, await verification.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM caddy_ui.ingestion_failures").SingleAsync());
        Assert.Equal(new FileInfo(path).Length, (await store.GetCheckpointAsync(path))?.ByteOffset);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    private sealed class CountingContextFactory(IDbContextFactory<CaddyUiDbContext> inner) : IDbContextFactory<CaddyUiDbContext>
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public bool FailNext { get; set; }

        public CaddyUiDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _count);
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Simulated persistence failure.");
            }
            return inner.CreateDbContext();
        }
    }
}
