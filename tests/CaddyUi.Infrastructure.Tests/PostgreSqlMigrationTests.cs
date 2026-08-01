using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Infrastructure.Security;
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
    public async Task Migrations_CreatePhaseSevenSchemaAndPartitions()
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

        Assert.Contains("20260727220000_PhaseOneFoundation", appliedMigrations);
        Assert.Contains("20260728220300_PhaseTwoMigrationPersistence", appliedMigrations);
        Assert.Contains("20260728230000_PhaseThreeAuthenticationAndDomainManagement", appliedMigrations);
        Assert.Contains("20260728240000_PhaseFourAnalyticsRuntime", appliedMigrations);
        Assert.Contains("20260728250000_PhaseFiveIpSecurity", appliedMigrations);
        Assert.Contains("20260728270000_PhaseSevenRouteManagement", appliedMigrations);
        Assert.Contains(
            "20260801190000_AccessPortalPresentationAndLoginScope",
            appliedMigrations);

        var routeStore = new RouteManagementStore(
            new RuntimeDbContextFactory(_postgres.GetConnectionString()));
        Assert.Empty(await routeStore.ListCredentialsAsync());

        var requiredTables = new[]
        {
            "caddy_ui.users",
            "caddy_ui.managed_routes",
            "caddy_ui.managed_domains",
            "caddy_ui.dns_providers",
            "caddy_ui.request_events",
            "caddy_ui.page_views",
            "caddy_ui.ip_intelligence_cache",
            "caddy_ui.ip_intelligence_refresh_queue",
            "caddy_ui.client_assessments",
            "caddy_ui.ip_block_rules",
            "caddy_ui.migration_runs",
            "caddy_ui.data_protection_keys",
            "caddy_ui.access_groups",
            "caddy_ui.access_credentials",
            "caddy_ui.route_revisions",
            "caddy_ui.apply_operations",
            "caddy_ui.apply_operation_steps",
            "caddy_ui.caddy_snapshots",
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

        var routePhaseSevenColumnsExist = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT COUNT(*) = 2 AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'caddy_ui'
                  AND table_name = 'managed_routes'
                  AND column_name IN ('access_group_id', 'sort_order')
                """)
            .SingleAsync();
        Assert.True(routePhaseSevenColumnsExist);

        var accessGroupColumnsExist = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT COUNT(*) = 2 AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'caddy_ui'
                  AND table_name = 'access_groups'
                  AND column_name IN ('enabled', 'description')
                """)
            .SingleAsync();
        Assert.True(accessGroupColumnsExist);

        var loginScopeColumnsAreText = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT COUNT(*) = 2 AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'caddy_ui'
                  AND table_name IN ('login_attempts', 'login_blocks')
                  AND column_name = 'scope'
                  AND data_type = 'text'
                """)
            .SingleAsync();
        Assert.True(loginScopeColumnsAreText);

        var authenticationStore = new AuthenticationStore(
            new RuntimeDbContextFactory(_postgres.GetConnectionString()));
        await authenticationStore.RecordLoginAttemptAsync(
            $"portal:{Guid.NewGuid():D}",
            "portal-test",
            "127.0.0.1",
            succeeded: true,
            reason: string.Empty);

        var routeAccessGroupForeignKeyExists = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM pg_constraint
                    WHERE conname = 'fk_managed_routes_access_group') AS "Value"
                """)
            .SingleAsync();
        Assert.True(routeAccessGroupForeignKeyExists);

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

        var retentionFunctionExists = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT to_regprocedure(
                    'caddy_ui.drop_expired_request_event_partitions(date)') IS NOT NULL AS "Value"
                """)
            .SingleAsync();
        Assert.True(retentionFunctionExists);

        var pageViewNavigationIndexExists = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT to_regclass(
                    'caddy_ui.ix_page_views_navigation') IS NOT NULL AS "Value"
                """)
            .SingleAsync();
        Assert.True(pageViewNavigationIndexExists);

        var blockActivationStateExists = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'caddy_ui'
                      AND table_name = 'ip_block_rules'
                      AND column_name = 'activation_state') AS "Value"
                """)
            .SingleAsync();
        Assert.True(blockActivationStateExists);

        database.DataProtectionKeys.Add(
            new DataProtectionKey
            {
                FriendlyName = "phase-seven-test",
                Xml = "<key />",
            });
        await database.SaveChangesAsync();

        Assert.Equal(1, await database.DataProtectionKeys.CountAsync());
    }
}
