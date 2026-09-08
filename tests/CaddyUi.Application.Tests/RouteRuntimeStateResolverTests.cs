using System.Text.Json;
using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Tests;

public sealed class RouteRuntimeStateResolverTests
{
    [Fact]
    public void Resolve_MarksMatchingFingerprintAsAppliedEvenWhenAnotherChangeIsPending()
    {
        var route = CreateRoute(enabled: true);
        var result = RouteRuntimeStateResolver.Resolve(
            [route],
            "desired-digest",
            Manifest((route.Id, "same", true)),
            "active-digest",
            Guid.NewGuid(),
            Manifest((route.Id, "same", true)),
            activeContentIsEmpty: false,
            lastApplyFailed: false,
            lastApplyError: string.Empty);

        Assert.True(result.HasPendingChanges);
        Assert.Equal(RouteRuntimeState.Applied, result.StateFor(route.Id));
    }

    [Fact]
    public void Resolve_MarksChangedFingerprintAsApplyRequired()
    {
        var route = CreateRoute(enabled: true);
        var result = RouteRuntimeStateResolver.Resolve(
            [route],
            "desired",
            Manifest((route.Id, "new", true)),
            "active",
            Guid.NewGuid(),
            Manifest((route.Id, "old", true)),
            activeContentIsEmpty: false,
            lastApplyFailed: false,
            lastApplyError: string.Empty);

        Assert.Equal(RouteRuntimeState.ApplyRequired, result.StateFor(route.Id));
    }

    [Fact]
    public void Resolve_MarksEnabledRouteWithoutActiveEntryAsNewDraft()
    {
        var route = CreateRoute(enabled: true);
        var result = RouteRuntimeStateResolver.Resolve(
            [route],
            "desired",
            Manifest((route.Id, "new", true)),
            "empty",
            activeRevisionId: null,
            activeManifestJson: null,
            activeContentIsEmpty: true,
            lastApplyFailed: false,
            lastApplyError: string.Empty);

        Assert.Equal(RouteRuntimeState.NewDraft, result.StateFor(route.Id));
    }

    [Fact]
    public void Resolve_MarksDisabledActiveRouteAsPendingRemoval()
    {
        var route = CreateRoute(enabled: false);
        var result = RouteRuntimeStateResolver.Resolve(
            [route],
            "desired",
            Manifest(),
            "active",
            Guid.NewGuid(),
            Manifest((route.Id, "active", true)),
            activeContentIsEmpty: false,
            lastApplyFailed: false,
            lastApplyError: string.Empty);

        Assert.Equal(RouteRuntimeState.PendingRemoval, result.StateFor(route.Id));
    }

    [Fact]
    public void Resolve_PreservesDeletedActiveRouteAsPendingRemovalReference()
    {
        var deletedId = Guid.NewGuid();
        var result = RouteRuntimeStateResolver.Resolve(
            Array.Empty<ManagedRouteDefinition>(),
            "desired",
            Manifest(),
            "active",
            Guid.NewGuid(),
            Manifest((deletedId, "active", true)),
            activeContentIsEmpty: false,
            lastApplyFailed: false,
            lastApplyError: string.Empty);

        var pending = Assert.Single(result.PendingRemovals);
        Assert.Equal(deletedId, pending.Id);
        Assert.Equal("app.example.com", pending.Host);
    }

    [Fact]
    public void Resolve_ReadsCompilerStyleManifestPropertyCasingForPendingRemoval()
    {
        var deletedId = Guid.NewGuid();
        var activeManifest = JsonSerializer.Serialize(new
        {
            schema = "managed-routes-v3",
            routes = new[]
            {
                new
                {
                    id = deletedId,
                    Name = "App",
                    Host = "app.example.com",
                    kind = "proxy",
                    path = "/",
                    fingerprint = "active",
                    generated = true,
                },
            },
        });

        var result = RouteRuntimeStateResolver.Resolve(
            Array.Empty<ManagedRouteDefinition>(),
            "desired",
            Manifest(),
            "active",
            Guid.NewGuid(),
            activeManifest,
            activeContentIsEmpty: false,
            lastApplyFailed: false,
            lastApplyError: string.Empty);

        var pending = Assert.Single(result.PendingRemovals);
        Assert.Equal("App", pending.Name);
        Assert.Equal("app.example.com", pending.Host);
    }

    [Fact]
    public void Resolve_UsesUnknownWhenActiveContentDoesNotMatchARevision()
    {
        var route = CreateRoute(enabled: true);
        var result = RouteRuntimeStateResolver.Resolve(
            [route],
            "desired",
            Manifest((route.Id, "desired", true)),
            "untracked",
            activeRevisionId: null,
            activeManifestJson: null,
            activeContentIsEmpty: false,
            lastApplyFailed: false,
            lastApplyError: string.Empty);

        Assert.False(result.ActiveStateTracked);
        Assert.Equal(RouteRuntimeState.Unknown, result.StateFor(route.Id));
        Assert.Empty(result.PendingRemovals);
    }

    [Fact]
    public void Resolve_SurfacesFailedApplyOnlyWhileDesiredStateIsStillPending()
    {
        var route = CreateRoute(enabled: true);
        var result = RouteRuntimeStateResolver.Resolve(
            [route],
            "desired",
            Manifest((route.Id, "new", true)),
            "active",
            Guid.NewGuid(),
            Manifest((route.Id, "old", true)),
            activeContentIsEmpty: false,
            lastApplyFailed: true,
            lastApplyError: "reload failed");

        Assert.True(result.LastApplyFailed);
        Assert.Equal("reload failed", result.LastApplyError);
    }

    private static ManagedRouteDefinition CreateRoute(bool enabled)
    {
        return ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "App",
            Guid.NewGuid(),
            "example.com",
            "app",
            ManagedRouteKind.Proxy,
            enabled,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with { Upstream = "app:8080" });
    }

    private static string Manifest(params (Guid Id, string Fingerprint, bool Generated)[] routes)
    {
        return JsonSerializer.Serialize(new
        {
            schema = "managed-routes-v3",
            routes = routes.Select(route => new
            {
                id = route.Id,
                name = "App",
                host = "app.example.com",
                kind = "proxy",
                path = "/",
                fingerprint = route.Fingerprint,
                generated = route.Generated,
            }),
        });
    }
}
