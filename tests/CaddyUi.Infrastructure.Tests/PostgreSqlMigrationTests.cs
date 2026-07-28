using CaddyUi.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
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
    public async Task Migrations_CreatePhaseThreeSchemaAndPartitions()
    {
        var options = new DbContextOptionsBuilder<CaddyUiDbContext>()
            .UseNpgsql(
                _postgres.GetConnectionString(),
                postgres => postgres.MigrationsHistoryTable(
                    "__EFMigrationsHistory",
                    "public"))
            .Options;

        await using var database = new CaddyUiDbContext(options);
        await database.Database.MigrateAsync();

        var appliedMigrations = (await database.Database
                .GetAppliedMigrationsAsync())
            .ToArray();

        Assert.Contains(
            "20260727220000_PhaseOneFoundation",
            appliedMigrations);
        Assert.Contains(
            "20260728220300_PhaseTwoMigrationPersistence",
            appliedMigrations);
        Assert.Contains(
            "20260728230000_PhaseThreeAuthenticationAndDomainManagement",
            appliedMigrations);

        var requiredTables = new[]
        {
            "caddy_ui.users",
            "caddy_ui.managed_routes",
            "caddy_ui.managed_domains",
            "caddy_ui.dns_providers",
            "caddy_ui.request_events",
            "caddy_ui.page_views",
            "caddy_ui.ip_intelligence_cache",
            "caddy_ui.migration_runs",
            "caddy_ui.data_protection_keys"
        };

        foreach (var table in requiredTables)
        {
            var exists = await database.Database
                .SqlQueryRaw<bool>(
                    "SELECT to_regclass({0}) IS NOT NULL AS \"Value\"",
                    table)
                .SingleAsync();
            Assert.True(exists, $"Expected PostgreSQL table {table}.");
        }

        var wildcardDefault = await database.Database
            .SqlQueryRaw<string>(
                """
                SELECT column_default AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'caddy_ui'
                  AND table_name = 'managed_domains'
                  AND column_name = 'default_certificate_mode'
                """)
            .SingleAsync();
        Assert.Contains("wildcard", wildcardDefault, StringComparison.Ordinal);

        var routeDomainColumnExists = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'caddy_ui'
                      AND table_name = 'managed_routes'
                      AND column_name = 'domain_id') AS "Value"
                """)
            .SingleAsync();
        Assert.True(routeDomainColumnExists);

        var isPartitioned = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM pg_partitioned_table partitioned
                    JOIN pg_class relation ON relation.oid = partitioned.partrelid
                    JOIN pg_namespace schema ON schema.oid = relation.relnamespace
                    WHERE schema.nspname = 'caddy_ui'
                      AND relation.relname = 'request_events') AS "Value"
                """)
            .SingleAsync();
        Assert.True(isPartitioned);

        database.DataProtectionKeys.Add(
            new DataProtectionKey
            {
                FriendlyName = "phase-three-test",
                Xml = "<key />"
            });
        await database.SaveChangesAsync();

        Assert.Equal(1, await database.DataProtectionKeys.CountAsync());
    }
}
