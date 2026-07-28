using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

public sealed class RouteTransferServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_route_transfer_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_route_transfer_tests")
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
    public async Task ExportAndImport_RoundTripsValidatedRoutesWithoutActivatingCaddy()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var domains = new DomainProviderStore(factory);
        var domainId = await domains.CreateDomainAsync(
            "example.com",
            "Example",
            dnsProviderId: null);
        var routes = new RouteManagementStore(factory);
        var actor = new ManagementActor(null, "route-transfer-test", "127.0.0.1");
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Mealie",
            domainId,
            "example.com",
            "mealie",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with
            {
                Upstream = "mealie:9925",
                HealthPath = "/health",
            });
        await routes.CreateRouteAsync(route, actor);

        var transfer = new RouteTransferService(
            routes,
            new RouteImportStore(factory),
            new RoutingOptions());
        var exported = await transfer.ExportAsync();

        Assert.Contains("caddy-ui-routes-v1", exported, StringComparison.Ordinal);
        Assert.Contains("\"domain\": \"example.com\"", exported, StringComparison.Ordinal);
        Assert.Contains("\"subdomain\": \"mealie\"", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("password", exported, StringComparison.OrdinalIgnoreCase);

        await routes.DeleteRouteAsync(route.Id, actor);
        var result = await transfer.ImportAsync(exported, actor);
        var imported = await routes.ListRoutesAsync();

        Assert.Equal(1, result.ImportedRoutes);
        var importedRoute = Assert.Single(imported).Definition;
        Assert.Equal("mealie.example.com", importedRoute.Host);
        Assert.Equal("mealie:9925", importedRoute.Configuration.Upstream);
        Assert.True(importedRoute.Enabled);
    }

    [Fact]
    public async Task Import_WithConflictingActiveTarget_IsAtomic()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var domains = new DomainProviderStore(factory);
        var domainId = await domains.CreateDomainAsync(
            "atomic.example",
            "Atomic",
            dnsProviderId: null);
        var routes = new RouteManagementStore(factory);
        var actor = new ManagementActor(null, "route-transfer-test", "127.0.0.1");
        var existing = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Existing",
            domainId,
            "atomic.example",
            "app",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with { Upstream = "existing:8080" });
        await routes.CreateRouteAsync(existing, actor);
        var transfer = new RouteTransferService(
            routes,
            new RouteImportStore(factory),
            new RoutingOptions());
        var json =
            """
            {
              "schema": "caddy-ui-routes-v1",
              "generatedAt": "2026-07-29T00:00:00Z",
              "routes": [
                {
                  "name": "Safe before conflict",
                  "domain": "atomic.example",
                  "subdomain": "safe",
                  "kind": "proxy",
                  "enabled": true,
                  "sortOrder": 0,
                  "certificateMode": "individual",
                  "accessGroup": "",
                  "configuration": {
                    "schema": "route-v1",
                    "pathPrefix": "/",
                    "upstream": "safe:8080",
                    "preserveHost": false,
                    "healthPath": "",
                    "healthIntervalSeconds": 30,
                    "redirectTarget": "",
                    "redirectPermanent": true,
                    "staticStatusCode": 200,
                    "staticBody": "",
                    "customSnippet": ""
                  }
                },
                {
                  "name": "Conflict",
                  "domain": "atomic.example",
                  "subdomain": "app",
                  "kind": "proxy",
                  "enabled": true,
                  "sortOrder": 0,
                  "certificateMode": "individual",
                  "accessGroup": "",
                  "configuration": {
                    "schema": "route-v1",
                    "pathPrefix": "/",
                    "upstream": "conflict:8080",
                    "preserveHost": false,
                    "healthPath": "",
                    "healthIntervalSeconds": 30,
                    "redirectTarget": "",
                    "redirectPermanent": true,
                    "staticStatusCode": 200,
                    "staticBody": "",
                    "customSnippet": ""
                  }
                }
              ]
            }
            """;

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.ImportAsync(json, actor));

        var remaining = await routes.ListRoutesAsync();
        Assert.Single(remaining);
        Assert.Equal(existing.Id, remaining[0].Definition.Id);
    }
}
