using System.Data.Common;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class RouteNameUniquenessMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_route_name_tests")
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
    public async Task RouteNames_AreUniquePerDomain_NotGlobally()
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

        var scopedIndexExists = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT to_regclass(
                    'caddy_ui.ix_managed_routes_domain_name_normalized') IS NOT NULL AS "Value"
                """)
            .SingleAsync();
        Assert.True(scopedIndexExists);

        var globalIndexExists = await database.Database
            .SqlQueryRaw<bool>(
                """
                SELECT to_regclass(
                    'caddy_ui.ix_managed_routes_name_normalized') IS NOT NULL AS "Value"
                """)
            .SingleAsync();
        Assert.False(globalIndexExists);

        var firstDomainId = Guid.NewGuid();
        var secondDomainId = Guid.NewGuid();
        var firstDomainName = "example.com";
        var secondDomainName = "example.net";

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO caddy_ui.managed_domains(
                id, name, display_name, created_at, updated_at)
            VALUES(
                {firstDomainId}, {firstDomainName}, {firstDomainName},
                CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            """);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO caddy_ui.managed_domains(
                id, name, display_name, created_at, updated_at)
            VALUES(
                {secondDomainId}, {secondDomainName}, {secondDomainName},
                CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            """);

        await InsertRouteAsync(
            database,
            Guid.NewGuid(),
            firstDomainId,
            "www",
            "www.example.com");
        await InsertRouteAsync(
            database,
            Guid.NewGuid(),
            secondDomainId,
            "WWW",
            "www.example.net");

        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertRouteAsync(
                database,
                Guid.NewGuid(),
                firstDomainId,
                "WWW",
                "other.example.com"));

        var routeStore = new RouteManagementStore(new TestDbContextFactory(options));
        var duplicateRoute = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "WWW",
            firstDomainId,
            firstDomainName,
            "other",
            ManagedRouteKind.Proxy,
            false,
            0,
            RouteCertificateMode.Inherit,
            null,
            RouteConfigurationDocument.Empty with
            {
                Upstream = "127.0.0.1:8080",
            });

        var validationError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            routeStore.CreateRouteAsync(duplicateRoute, ManagementActor.System));
        Assert.Equal(
            "A route named 'WWW' already exists in example.com.",
            validationError.Message);
    }

    private static Task InsertRouteAsync(
        CaddyUiDbContext database,
        Guid id,
        Guid domainId,
        string name,
        string host)
    {
        return database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO caddy_ui.managed_routes(
                id, name, host, kind, enabled, config_json,
                created_at, updated_at, domain_id, subdomain, certificate_mode)
            VALUES(
                {id}, {name}, {host}, 'proxy', false, jsonb_build_object(),
                CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, {domainId}, 'www', 'inherit')
            """);
    }

    private sealed class TestDbContextFactory(DbContextOptions<CaddyUiDbContext> options)
        : IDbContextFactory<CaddyUiDbContext>
    {
        public CaddyUiDbContext CreateDbContext()
        {
            return new CaddyUiDbContext(options);
        }
    }
}
