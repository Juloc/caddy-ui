using System.Data;
using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class HealthOperationsStoreTests : IAsyncLifetime
{
    private static readonly string[] HealthPersistenceMethods =
    [
        "ListHealthTargetsAsync",
        "CreateHealthTargetAsync",
        "SetHealthTargetEnabledAsync",
        "RecordHealthCheckAsync",
    ];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_health_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_health_tests")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public void HealthOperationsStore_OwnsHealthPersistenceBoundary()
    {
        var healthMethods = typeof(HealthOperationsStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var method in HealthPersistenceMethods)
        {
            Assert.Contains(method, healthMethods);
        }
    }

    [Fact]
    public async Task HealthCheck_UpdatesTargetAndHistoryInOnePersistenceFlow()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var store = new HealthOperationsStore(factory);
        var targetId = await store.CreateHealthTargetAsync(
            "Public endpoint",
            "public",
            "https://example.com/health",
            200,
            299,
            5);

        var created = Assert.Single(await store.ListHealthTargetsAsync());
        Assert.Equal(targetId, created.Id);
        Assert.True(created.Enabled);
        Assert.Equal("public", created.TargetType);

        await store.RecordHealthCheckAsync(
            targetId,
            ProviderOperationResult.Success("HTTP 204"),
            204,
            12.5);

        var updated = Assert.Single(await store.ListHealthTargetsAsync());
        Assert.Equal("healthy", updated.LastStatus);
        Assert.Equal(204, updated.LastHttpStatus);
        Assert.Equal(12.5, updated.LastDurationMilliseconds);
        Assert.NotNull(updated.LastCheckedAt);
        Assert.Equal(string.Empty, updated.LastError);

        await using (var database = factory.CreateDbContext())
        {
            var connection = database.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT status, http_status, duration_ms, error
                FROM caddy_ui.health_checks
                WHERE target_id = @target_id
                ORDER BY checked_at DESC
                LIMIT 1
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "target_id";
            parameter.Value = targetId;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("healthy", reader.GetString(0));
            Assert.Equal(204, reader.GetInt32(1));
            Assert.Equal(12.5, reader.GetDouble(2));
            Assert.Equal(string.Empty, reader.GetString(3));
        }

        await store.SetHealthTargetEnabledAsync(targetId, false);
        Assert.False((await store.ListHealthTargetsAsync()).Single().Enabled);
    }
}
