using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages.Routing;

[Authorize(Policy = "Editor")]
public sealed class IndexModel : PageModel
{
    private readonly RouteManagementStore _store;
    private readonly CaddyApplyService _applyService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public IndexModel(
        RouteManagementStore store,
        CaddyApplyService applyService,
        IStringLocalizer<SharedResource> localizer)
    {
        _store = store;
        _applyService = applyService;
        _localizer = localizer;
    }

    public IReadOnlyList<ManagedRouteRecord> Routes { get; private set; } =
        Array.Empty<ManagedRouteRecord>();

    public RoutingOptions RoutingOptions => _applyService.Options;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        Routes = await _store.ListRoutesAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, bool enabled)
    {
        var existing = await _store.GetRouteAsync(id, HttpContext.RequestAborted);
        if (existing is null)
        {
            return NotFound();
        }

        try
        {
            await _store.UpdateRouteAsync(
                existing.Definition with { Enabled = enabled },
                User.ToManagementActor(HttpContext),
                HttpContext.RequestAborted);
            StatusMessage = enabled ? _localizer["Route enabled."] : _localizer["Route disabled."];
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Routes = await _store.ListRoutesAsync(HttpContext.RequestAborted);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        try
        {
            await _store.DeleteRouteAsync(
                id,
                User.ToManagementActor(HttpContext),
                HttpContext.RequestAborted);
            StatusMessage = _localizer[
                "Route deleted. The active Caddy configuration changes only after preview and apply."];
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Routes = await _store.ListRoutesAsync(HttpContext.RequestAborted);
            return Page();
        }
    }
}
