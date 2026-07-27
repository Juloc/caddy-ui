using CaddyUi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class PostgreSqlMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_tests")
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
    public async Task InitialMigration_CreatesFoundationSchema()
    {
        var options = new DbContextOptionsBuilder<CaddyUiDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var database = new CaddyUiDbContext(options);
        await database.Database.MigrateAsync();

        var appliedMigrations = await database.Database
            .GetAppliedMigrationsAsync();

        Assert.Contains(
            "20260727220000_PhaseOneFoundation",
            appliedMigrations);
        Assert.True(await database.Database.CanConnectAsync());
    }
}
