using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

[CollectionDefinition("Route runtime state integration", DisableParallelization = true)]
public sealed class RouteRuntimeStateIntegrationCollection
{
    public const string CollectionName = "Route runtime state integration";
}

[Collection(RouteRuntimeStateIntegrationCollection.CollectionName)]
public sealed class RouteRuntimeStateIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_route_runtime_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_route_runtime_tests")
        .Build();

    public Task InitializeAsync()
    {
        return _postgres.StartAsync();
    }

    public Task DisposeAsync()
    {
        CaddyCertificateSourceRegistry.Clear();
        return _postgres.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task RouteLifecycle_TracksAppliedPendingFailedAndRemovedState()
    {
        CaddyCertificateSourceRegistry.Clear();
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var temporaryDirectory = Directory.CreateTempSubdirectory("caddy-ui-route-state-");
        try
        {
            var managedPath = Path.Combine(temporaryDirectory.FullName, "managed-routes.caddy");
            var shadowPath = Path.Combine(temporaryDirectory.FullName, "shadow-routes.caddy");
            var rootPath = Path.Combine(temporaryDirectory.FullName, "Caddyfile");
            await File.WriteAllTextAsync(
                rootPath,
                $"import \"{managedPath.Replace('\\', '/')}\"\n");

            var options = new RoutingOptions
            {
                WriteMode = RouteWriteMode.Active,
                ManagedFragmentPath = managedPath,
                ShadowFragmentPath = shadowPath,
                RootConfigPath = rootPath,
                PortalUpstream = "127.0.0.1:8099",
            };
            var routeStore = new RouteManagementStore(factory);
            var applyStore = new RouteApplyStore(factory);
            var runner = new ControlledCaddyCommandRunner();
            var apply = new CaddyApplyService(routeStore, applyStore, options, runner);
            var runtime = new RouteRuntimeStateService(routeStore, applyStore, apply, options);
            var domains = new DomainProviderStore(factory);
            var actor = new ManagementActor(null, "route-state-test", "127.0.0.1");

            var domainId = await domains.CreateDomainAsync(
                "example.com",
                "Example",
                dnsProviderId: null);
            var desired = ManagedRouteDefinition.Create(
                Guid.NewGuid(),
                "App",
                domainId,
                "example.com",
                "app",
                ManagedRouteKind.Proxy,
                true,
                0,
                RouteCertificateMode.Individual,
                null,
                RouteConfigurationDocument.Empty with { Upstream = "app:8080" });
            await routeStore.CreateRouteAsync(desired, actor);

            var state = await runtime.GetAsync();
            Assert.Equal(RouteRuntimeState.NewDraft, state.StateFor(desired.Id));
            Assert.True(state.HasPendingChanges);
            Assert.Empty(await applyStore.ListRevisionsAsync());
            Assert.Empty(await applyStore.ListOperationsAsync());

            var initialPreview = await apply.CreatePreviewAsync("Initial route", actor);
            var initialApply = await apply.ApplyAsync(initialPreview.Revision.Id, actor);
            Assert.Equal("applied", initialApply.State);
            Assert.NotEmpty(await applyStore.ListRevisionsAsync());
            Assert.Contains(
                await applyStore.ListOperationsAsync(),
                operation => operation.State == "applied");

            state = await runtime.GetAsync();
            Assert.Equal(RouteRuntimeState.Applied, state.StateFor(desired.Id));
            Assert.False(state.HasPendingChanges);
            Assert.Equal(initialPreview.Revision.Id, state.ActiveRevisionId);

            desired = desired with
            {
                Configuration = desired.Configuration with { Upstream = "app-v2:8080" },
            };
            await routeStore.UpdateRouteAsync(desired, actor);

            state = await runtime.GetAsync();
            Assert.Equal(RouteRuntimeState.ApplyRequired, state.StateFor(desired.Id));
            Assert.True(state.HasPendingChanges);

            var failedPreview = await apply.CreatePreviewAsync("Update route", actor);
            runner.FailNextReload = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                apply.ApplyAsync(failedPreview.Revision.Id, actor));

            state = await runtime.GetAsync();
            Assert.True(state.LastApplyFailed);
            Assert.Equal(RouteRuntimeState.ApplyRequired, state.StateFor(desired.Id));
            Assert.Equal(initialPreview.Revision.Id, state.ActiveRevisionId);
            Assert.NotEmpty(state.LastApplyError);

            desired = desired with { Enabled = false };
            await routeStore.UpdateRouteAsync(desired, actor);

            state = await runtime.GetAsync();
            Assert.False(state.LastApplyFailed);
            Assert.Equal(RouteRuntimeState.PendingRemoval, state.StateFor(desired.Id));

            var disablePreview = await apply.CreatePreviewAsync("Disable route", actor);
            await apply.ApplyAsync(disablePreview.Revision.Id, actor);

            state = await runtime.GetAsync();
            Assert.Equal(RouteRuntimeState.Disabled, state.StateFor(desired.Id));
            Assert.False(state.HasPendingChanges);

            desired = desired with { Enabled = true };
            await routeStore.UpdateRouteAsync(desired, actor);
            state = await runtime.GetAsync();
            Assert.Equal(RouteRuntimeState.NewDraft, state.StateFor(desired.Id));

            var enablePreview = await apply.CreatePreviewAsync("Enable route", actor);
            await apply.ApplyAsync(enablePreview.Revision.Id, actor);
            Assert.Equal(RouteRuntimeState.Applied, (await runtime.GetAsync()).StateFor(desired.Id));

            await routeStore.DeleteRouteAsync(desired.Id, actor);

            state = await runtime.GetAsync();
            var pendingRemoval = Assert.Single(state.PendingRemovals);
            Assert.Equal(desired.Id, pendingRemoval.Id);
            Assert.Equal("app.example.com", pendingRemoval.Host);
            Assert.Equal(1, state.ActiveRouteCount);

            var deletePreview = await apply.CreatePreviewAsync("Remove deleted route", actor);
            await apply.ApplyAsync(deletePreview.Revision.Id, actor);

            state = await runtime.GetAsync();
            Assert.Empty(state.PendingRemovals);
            Assert.False(state.HasPendingChanges);
            Assert.Equal(0, state.ActiveRouteCount);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    private sealed class ControlledCaddyCommandRunner : ICaddyCommandRunner
    {
        public bool FailNextReload { get; set; }

        public Task<CaddyCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (FailNextReload &&
                arguments.Count > 0 &&
                string.Equals(arguments[0], "reload", StringComparison.Ordinal))
            {
                FailNextReload = false;
                return Task.FromResult(new CaddyCommandResult(
                    1,
                    string.Empty,
                    "simulated reload failure",
                    false));
            }

            return Task.FromResult(new CaddyCommandResult(
                0,
                string.Empty,
                string.Empty,
                false));
        }
    }
}
