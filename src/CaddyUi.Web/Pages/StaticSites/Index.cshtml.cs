using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages.StaticSites;

[Authorize(Policy = "Editor")]
public sealed class IndexModel : LocalizedPageModel
{
    private readonly RouteManagementStore _store;
    private readonly RouteRuntimeStateService _runtimeStateService;
    private readonly IStringLocalizer<StaticSitesResource> _localizer;

    public IndexModel(
        RouteManagementStore store,
        RouteRuntimeStateService runtimeStateService,
        IStringLocalizer<StaticSitesResource> localizer)
    {
        _store = store;
        _runtimeStateService = runtimeStateService;
        _localizer = localizer;
    }

    public IReadOnlyList<ManagedRouteRecord> Sites { get; private set; } =
        Array.Empty<ManagedRouteRecord>();

    public RoutingRuntimeState RuntimeState { get; private set; } = null!;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var existing = await _store.GetRouteAsync(id, HttpContext.RequestAborted);
        if (existing is null || existing.Definition.Kind != ManagedRouteKind.StaticSite)
        {
            return NotFound();
        }

        await _store.DeleteRouteAsync(
            id,
            User.ToManagementActor(HttpContext),
            HttpContext.RequestAborted);

        var runtimeState = await _runtimeStateService.GetAsync(HttpContext.RequestAborted);
        StatusMessage = !runtimeState.ActiveStateTracked
            ? _localizer["Static site deleted. Review and apply the saved configuration to remove it from Caddy."]
            : runtimeState.PendingRemovals.Any(route => route.Id == id)
                ? _localizer["Static site deleted. It remains active until the saved changes are applied."]
                : _localizer["Static site deleted."];
        return RedirectToPage();
    }

    public RouteRuntimeState StateFor(Guid routeId)
    {
        return RuntimeState.StateFor(routeId);
    }

    public static string StateLabelKey(RouteRuntimeState state)
    {
        return state switch
        {
            RouteRuntimeState.Applied => "Active",
            RouteRuntimeState.ApplyRequired => "Apply required",
            RouteRuntimeState.NewDraft => "New · not active",
            RouteRuntimeState.PendingRemoval => "Removal pending",
            RouteRuntimeState.Disabled => "Disabled",
            _ => "Status unknown",
        };
    }

    public static string StateBadgeClass(RouteRuntimeState state)
    {
        return state switch
        {
            RouteRuntimeState.Applied => "status-badge--ok",
            RouteRuntimeState.ApplyRequired or RouteRuntimeState.NewDraft or RouteRuntimeState.PendingRemoval =>
                "status-badge--warning",
            _ => "status-badge--neutral",
        };
    }

    public static bool CanOpen(ManagedRouteDefinition route, RouteRuntimeState state)
    {
        return !route.Host.StartsWith("*.", StringComparison.Ordinal) &&
               state is RouteRuntimeState.Applied or RouteRuntimeState.ApplyRequired or RouteRuntimeState.PendingRemoval;
    }

    public static string PublicUrl(ManagedRouteDefinition route)
    {
        return $"https://{route.Host}/";
    }

    private async Task LoadAsync()
    {
        Sites = (await _store.ListRoutesAsync(HttpContext.RequestAborted))
            .Where(route => route.Definition.Kind == ManagedRouteKind.StaticSite)
            .OrderBy(route => route.Definition.Host, StringComparer.Ordinal)
            .ToArray();
        RuntimeState = await _runtimeStateService.GetAsync(HttpContext.RequestAborted);
    }
}
