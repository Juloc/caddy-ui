using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class ScheduledJobOperationsStoreTests : IAsyncLifetime
{
    private static readonly string[] ScheduledJobPersistenceMethods =
    [
        "ListJobsAsync",
        "ListJobRunsAsync",
        "CreateJobAsync",
        "SetJobEnabledAsync",
        "ClaimDueJobAsync",
        "StartJobRunAsync",
        "CompleteJobRunAsync",
    ];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_scheduled_job_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_scheduled_job_tests")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public void ScheduledJobOperationsStore_OwnsScheduledJobPersistenceBoundary()
    {
        var operationsMethods = typeof(OperationsStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var scheduledJobMethods = typeof(ScheduledJobOperationsStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var method in ScheduledJobPersistenceMethods)
        {
            Assert.DoesNotContain(method, operationsMethods);
            Assert.Contains(method, scheduledJobMethods);
        }
    }

    [Fact]
    public async Task ScheduledJobClaimAndCompletion_PreserveLifecycleAndExclusiveClaiming()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var store = new ScheduledJobOperationsStore(factory);
        var jobId = await store.CreateJobAsync(
            "Health sweep",
            "health",
            300,
            "{}");

        var claimed = await store.ClaimDueJobAsync("worker-a");
        var second = await store.ClaimDueJobAsync("worker-b");

        Assert.NotNull(claimed);
        Assert.Equal(jobId, claimed.Id);
        Assert.Null(second);
        Assert.Equal("running", (await store.ListJobsAsync()).Single().LastStatus);

        var runId = await store.StartJobRunAsync(jobId, "correlation-1");
        await store.CompleteJobRunAsync(
            jobId,
            runId,
            ProviderOperationResult.Success("Health sweep complete."),
            "{\"checked\":1}");

        var job = Assert.Single(await store.ListJobsAsync());
        Assert.Equal("ok", job.LastStatus);
        Assert.NotNull(job.LastRunAt);
        Assert.Equal(string.Empty, job.LastError);

        var run = Assert.Single(await store.ListJobRunsAsync());
        Assert.Equal(runId, run.Id);
        Assert.Equal(jobId, run.JobId);
        Assert.Equal("ok", run.Status);
        Assert.Equal("Health sweep complete.", run.Message);
        Assert.Equal("correlation-1", run.CorrelationId);
        Assert.NotNull(run.CompletedAt);
    }
}
