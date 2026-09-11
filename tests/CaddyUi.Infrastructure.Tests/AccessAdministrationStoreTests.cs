using System.Reflection;
using CaddyUi.Infrastructure.Persistence;
using CaddyUi.Infrastructure.Routing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CaddyUi.Infrastructure.Tests;

[CollectionDefinition("Access administration integration", DisableParallelization = true)]
public sealed class AccessAdministrationIntegrationCollection
{
    public const string CollectionName = "Access administration integration";
}

[Collection(AccessAdministrationIntegrationCollection.CollectionName)]
public sealed class AccessAdministrationStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("caddy_ui_access_tests")
        .WithUsername("caddy_ui")
        .WithPassword("caddy_ui_access_tests")
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
    public void AccessPersistence_IsOwnedByAccessAdministrationStore()
    {
        var accessMethods = new[]
        {
            nameof(AccessAdministrationStore.ListAccessGroupsAsync),
            nameof(AccessAdministrationStore.ListCredentialsAsync),
            nameof(AccessAdministrationStore.CreateAccessGroupAsync),
            nameof(AccessAdministrationStore.SetAccessGroupEnabledAsync),
            nameof(AccessAdministrationStore.CreateCredentialAsync),
            nameof(AccessAdministrationStore.SetCredentialEnabledAsync),
        };
        var accessStoreMethods = typeof(AccessAdministrationStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var routeStoreMethods = typeof(RouteManagementStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var method in accessMethods)
        {
            Assert.Contains(method, accessStoreMethods);
            Assert.DoesNotContain(method, routeStoreMethods);
        }
    }

    [Fact]
    public async Task AccessLifecycle_CreateListToggleUpdateAndDelete_RemainsConsistent()
    {
        var factory = new RuntimeDbContextFactory(_postgres.GetConnectionString());
        await using (var database = factory.CreateDbContext())
        {
            await database.Database.MigrateAsync();
        }

        var store = new AccessAdministrationStore(factory);
        var actor = new ManagementActor(null, "access-store-test", "127.0.0.1");

        var groupId = await store.CreateAccessGroupAsync(
            "Editors",
            "Protected apps",
            "#334455",
            "https://example.com/icon.svg",
            actor);

        var group = Assert.Single(await store.ListAccessGroupsAsync());
        Assert.Equal(groupId, group.Id);
        Assert.Equal("Editors", group.Name);
        Assert.Equal("Protected apps", group.Description);
        Assert.Equal("#334455", group.AccentColor);
        Assert.Equal("https://example.com/icon.svg", group.IconUrl);
        Assert.True(group.Enabled);
        Assert.Equal(0, group.CredentialCount);
        Assert.Equal(0, group.RouteCount);

        await store.SetAccessGroupEnabledAsync(groupId, false);
        group = Assert.Single(await store.ListAccessGroupsAsync());
        Assert.False(group.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCredentialAsync(groupId, "portal-user", "hash-value", actor));

        await store.SetAccessGroupEnabledAsync(groupId, true);
        var credentialId = await store.CreateCredentialAsync(
            groupId,
            "portal-user",
            "hash-value",
            actor);

        var credential = Assert.Single(await store.ListCredentialsAsync(groupId));
        Assert.Equal(credentialId, credential.Id);
        Assert.Equal(groupId, credential.GroupId);
        Assert.Equal("portal-user", credential.Username);
        Assert.True(credential.Enabled);

        group = Assert.Single(await store.ListAccessGroupsAsync());
        Assert.Equal(1, group.CredentialCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DeleteGroupAsync(groupId, actor));

        await store.SetCredentialEnabledAsync(credentialId, false);
        credential = Assert.Single(await store.ListCredentialsAsync(groupId));
        Assert.False(credential.Enabled);

        await store.UpdateCredentialAsync(credentialId, "portal-user-2", null, actor);
        credential = Assert.Single(await store.ListCredentialsAsync(groupId));
        Assert.Equal("portal-user-2", credential.Username);

        await store.UpdateGroupAsync(
            groupId,
            "Editors 2",
            "Updated protected apps",
            "#445566",
            "https://example.com/icon-2.svg",
            actor);
        group = Assert.Single(await store.ListAccessGroupsAsync());
        Assert.Equal("Editors 2", group.Name);
        Assert.Equal("Updated protected apps", group.Description);
        Assert.Equal("#445566", group.AccentColor);
        Assert.Equal("https://example.com/icon-2.svg", group.IconUrl);

        await store.DeleteCredentialAsync(credentialId, actor);
        Assert.Empty(await store.ListCredentialsAsync(groupId));

        await store.DeleteGroupAsync(groupId, actor);
        Assert.Empty(await store.ListAccessGroupsAsync());
    }
}
