using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;
using CaddyUi.Web.Pages.Routing;

namespace CaddyUi.Web.Tests;

public sealed class RouteRuntimePresentationTests
{
    [Theory]
    [InlineData(RouteRuntimeState.Applied, true)]
    [InlineData(RouteRuntimeState.ApplyRequired, true)]
    [InlineData(RouteRuntimeState.PendingRemoval, true)]
    [InlineData(RouteRuntimeState.NewDraft, false)]
    [InlineData(RouteRuntimeState.Disabled, false)]
    [InlineData(RouteRuntimeState.Unknown, false)]
    public void CanOpen_UsesActualRuntimePresence(
        RouteRuntimeState state,
        bool expected)
    {
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "App",
            Guid.NewGuid(),
            "example.com",
            "app",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with { Upstream = "app:8080" });

        Assert.Equal(expected, IndexModel.CanOpen(route, state));
    }

    [Fact]
    public void CanOpen_RejectsWildcardHostsEvenWhenApplied()
    {
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Wildcard",
            Guid.NewGuid(),
            "example.com",
            "*",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with { Upstream = "app:8080" });

        Assert.False(IndexModel.CanOpen(route, RouteRuntimeState.Applied));
    }

    [Theory]
    [InlineData(RouteRuntimeState.Applied, "Aktiv")]
    [InlineData(RouteRuntimeState.ApplyRequired, "Änderung offen")]
    [InlineData(RouteRuntimeState.NewDraft, "Neu · nicht aktiv")]
    [InlineData(RouteRuntimeState.PendingRemoval, "Entfernung offen")]
    [InlineData(RouteRuntimeState.Disabled, "Deaktiviert")]
    [InlineData(RouteRuntimeState.Unknown, "Status unbekannt")]
    public void StateLabel_DescribesRuntimeState(
        RouteRuntimeState state,
        string expected)
    {
        Assert.Equal(expected, IndexModel.StateLabel(state));
    }
}
