using System.ComponentModel.DataAnnotations;
using System.Text;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages.Routing;

[Authorize(Policy = "Editor")]
public sealed class IndexModel : LocalizedPageModel
{
    private readonly RouteManagementStore _store;
    private readonly CaddyApplyService _applyService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        RouteManagementStore store,
        CaddyApplyService applyService,
        IStringLocalizer<SharedResource> localizer,
        ILogger<IndexModel> logger)
    {
        _store = store;
        _applyService = applyService;
        _localizer = localizer;
        _logger = logger;
    }

    public IReadOnlyList<ManagedRouteRecord> Routes { get; private set; } =
        Array.Empty<ManagedRouteRecord>();

    public IReadOnlyList<DomainRouteGroup> DomainGroups { get; private set; } =
        Array.Empty<DomainRouteGroup>();

    [BindProperty]
    public QuickRouteInput QuickRoute { get; set; } = new();

    public RoutingOptions RoutingOptions => _applyService.Options;

    public Guid? QuickCreateDialogDomainId { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(Guid? quickDomain)
    {
        QuickCreateDialogDomainId = quickDomain;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostQuickCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            QuickCreateDialogDomainId = QuickRoute.DomainId == Guid.Empty ? null : QuickRoute.DomainId;
            await LoadAsync();
            return Page();
        }

        var actor = User.ToManagementActor(HttpContext);
        ManagedRouteDefinition? definition = null;
        var routeSaved = false;

        try
        {
            var domains = await _store.ListDomainsAsync(HttpContext.RequestAborted);
            var domain = domains.FirstOrDefault(item => item.Id == QuickRoute.DomainId && item.Enabled) ??
                throw new InvalidOperationException(_localizer["The selected domain does not exist or is disabled."]);
            var subdomain = CreateSubdomain(QuickRoute.Name);
            if (subdomain.Length == 0)
            {
                throw new InvalidOperationException(
                    _localizer["The route name must contain at least one letter or number for the subdomain."]);
            }

            var routes = await _store.ListRoutesAsync(HttpContext.RequestAborted);
            if (routes.Any(route =>
                    route.Definition.DomainId == domain.Id &&
                    string.Equals(route.Definition.Subdomain, subdomain, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(route.Definition.Configuration.PathPrefix, "/", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    _localizer["A root route for {0}.{1} already exists.", subdomain, domain.Name]);
            }

            definition = ManagedRouteDefinition.Create(
                Guid.NewGuid(),
                QuickRoute.Name,
                domain.Id,
                domain.Name,
                subdomain,
                ManagedRouteKind.Proxy,
                true,
                0,
                RouteCertificateMode.Wildcard,
                null,
                RouteConfigurationDocument.Empty with { Upstream = QuickRoute.Upstream });

            await _store.CreateRouteAsync(definition, actor, HttpContext.RequestAborted);
            routeSaved = true;

            var preview = await _applyService.CreatePreviewAsync(
                $"Create and activate quick route {definition.Name}",
                actor,
                HttpContext.RequestAborted);
            var result = await _applyService.ApplyAsync(
                preview.Revision.Id,
                actor,
                HttpContext.RequestAborted);

            StatusMessage = _localizer[
                "Route created and activated: {0}",
                result.Message];
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Quick route creation failed for domain {DomainId}.",
                QuickRoute.DomainId);

            if (routeSaved && definition is not null)
            {
                try
                {
                    await _store.UpdateRouteAsync(
                        definition with { Enabled = false },
                        actor,
                        HttpContext.RequestAborted);
                    ErrorMessage = _localizer[
                        "The route was saved but could not be activated and remains disabled: {0}",
                        exception.Message];
                    return RedirectToPage();
                }
                catch (Exception disableException) when (disableException is not OperationCanceledException)
                {
                    _logger.LogError(
                        disableException,
                        "Failed to disable quick route {RouteId} after activation error.",
                        definition.Id);
                }
            }

            ModelState.AddModelError(string.Empty, exception.Message);
            QuickCreateDialogDomainId = QuickRoute.DomainId == Guid.Empty ? null : QuickRoute.DomainId;
            await LoadAsync();
            return Page();
        }
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
            await LoadAsync();
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
            await LoadAsync();
            return Page();
        }
    }

    public static string PublicUrl(ManagedRouteDefinition route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return $"https://{route.Host}{route.Configuration.PathPrefix}";
    }

    public static bool CanOpen(ManagedRouteDefinition route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Enabled && !route.Host.StartsWith("*.", StringComparison.Ordinal);
    }

    private async Task LoadAsync()
    {
        Routes = await _store.ListRoutesAsync(HttpContext.RequestAborted);
        var domains = await _store.ListDomainsAsync(HttpContext.RequestAborted);
        DomainGroups = domains
            .OrderByDescending(domain => domain.IsDefault)
            .ThenBy(domain => domain.Name, StringComparer.Ordinal)
            .Select(domain => new DomainRouteGroup(
                domain,
                Routes
                    .Where(route => route.Definition.DomainId == domain.Id)
                    .OrderBy(route => route.Definition.Subdomain, StringComparer.Ordinal)
                    .ThenBy(route => route.Definition.SortOrder)
                    .ThenBy(route => route.Definition.Name, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
    }

    private static string CreateSubdomain(string routeName)
    {
        var builder = new StringBuilder(capacity: Math.Min(routeName.Length, 63));
        var separatorPending = false;

        foreach (var character in routeName.Trim().ToLowerInvariant())
        {
            var allowed = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (!allowed)
            {
                separatorPending = builder.Length > 0;
                continue;
            }

            if (separatorPending && builder.Length < 63)
            {
                builder.Append('-');
            }

            if (builder.Length >= 63)
            {
                break;
            }

            builder.Append(character);
            separatorPending = false;
        }

        return builder.ToString().TrimEnd('-');
    }

    public sealed record DomainRouteGroup(
        ManagedDomainOption Domain,
        IReadOnlyList<ManagedRouteRecord> Routes);

    public sealed class QuickRouteInput
    {
        [Required]
        public Guid DomainId { get; set; }

        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [MaxLength(2048)]
        public string Upstream { get; set; } = string.Empty;
    }
}
