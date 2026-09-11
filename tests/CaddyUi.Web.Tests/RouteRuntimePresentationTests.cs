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
    [InlineData(RouteRuntimeState.Applied, "Active")]
    [InlineData(RouteRuntimeState.ApplyRequired, "Apply required")]
    [InlineData(RouteRuntimeState.NewDraft, "New · not active")]
    [InlineData(RouteRuntimeState.PendingRemoval, "Removal pending")]
    [InlineData(RouteRuntimeState.Disabled, "Disabled")]
    [InlineData(RouteRuntimeState.Unknown, "Status unknown")]
    public void StateLabelKey_DescribesRuntimeState(
        RouteRuntimeState state,
        string expected)
    {
        Assert.Equal(expected, IndexModel.StateLabelKey(state));
    }
}
