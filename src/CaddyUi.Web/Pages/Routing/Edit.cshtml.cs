using System.ComponentModel.DataAnnotations;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages.Routing;

[Authorize(Policy = "Editor")]
public sealed class EditModel : LocalizedPageModel
{
    private readonly RouteManagementStore _store;
    private readonly CaddyApplyService _applyService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public EditModel(
        RouteManagementStore store,
        CaddyApplyService applyService,
        IStringLocalizer<SharedResource> localizer)
    {
        _store = store;
        _applyService = applyService;
        _localizer = localizer;
    }

    [BindProperty]
    public RouteInput Input { get; set; } = new();

    public IReadOnlyList<ManagedDomainOption> Domains { get; private set; } =
        Array.Empty<ManagedDomainOption>();

    public IReadOnlyList<AccessGroupRecord> AccessGroups { get; private set; } =
        Array.Empty<AccessGroupRecord>();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public bool IsEdit => Input.Id is not null;

    public bool AllowCustomRoutes => _applyService.Options.AllowCustomRoutes;

    public async Task<IActionResult> OnGetAsync(Guid? id)
    {
        await LoadOptionsAsync();
        if (id is null)
        {
            var defaultDomain = Domains.FirstOrDefault(domain => domain.IsDefault && domain.Enabled) ??
                Domains.FirstOrDefault(domain => domain.Enabled);
            Input.DomainId = defaultDomain?.Id ?? Guid.Empty;
            return Page();
        }

        var route = await _store.GetRouteAsync(id.Value, HttpContext.RequestAborted);
        if (route is null)
        {
            return NotFound();
        }

        Input = RouteInput.FromRecord(route);
        return Page();
    }

    public Task<IActionResult> OnPostAsync()
    {
        return OnPostSaveAsync();
    }

    public Task<IActionResult> OnPostSaveAsync()
    {
        return SaveAsync(applyAfterSave: false);
    }

    public Task<IActionResult> OnPostSaveApplyAsync()
    {
        return SaveAsync(applyAfterSave: true);
    }

    private async Task<IActionResult> SaveAsync(bool applyAfterSave)
    {
        await LoadOptionsAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var routeWasSaved = false;
        Guid? savedRouteId = null;
        try
        {
            var domain = Domains.FirstOrDefault(item => item.Id == Input.DomainId && item.Enabled) ??
                throw new InvalidOperationException(_localizer["The selected domain does not exist or is disabled."]);
            var kind = ManagedRouteDefinition.ParseKind(Input.Kind);
            if (kind == ManagedRouteKind.Custom && !AllowCustomRoutes)
            {
                throw new InvalidOperationException(_localizer["Custom Caddy routes are disabled in configuration."]);
            }

            var configuration = new RouteConfigurationDocument(
                "route-v1",
                Input.PathPrefix,
                Input.Upstream,
                Input.PreserveHost,
                Input.HealthPath,
                Input.HealthIntervalSeconds,
                Input.RedirectTarget,
                Input.RedirectPermanent,
                Input.StaticStatusCode,
                Input.StaticBody,
                Input.CustomSnippet);
            var routeId = Input.Id ?? Guid.NewGuid();
            var definition = ManagedRouteDefinition.Create(
                routeId,
                Input.Name,
                domain.Id,
                domain.Name,
                Input.Subdomain,
                kind,
                applyAfterSave || Input.Enabled,
                Input.SortOrder,
                ManagedRouteDefinition.ParseCertificateMode(Input.CertificateMode),
                Input.AccessGroupId,
                configuration);
            var actor = User.ToManagementActor(HttpContext);
            var isNew = Input.Id is null;
            if (isNew)
            {
                await _store.CreateRouteAsync(definition, actor, HttpContext.RequestAborted);
            }
            else
            {
                await _store.UpdateRouteAsync(definition, actor, HttpContext.RequestAborted);
            }

            routeWasSaved = true;
            savedRouteId = routeId;
            Input.Id = routeId;

            if (!applyAfterSave)
            {
                StatusMessage = isNew
                    ? _localizer["Route created. The active Caddy configuration is unchanged."]
                    : _localizer["Route saved. The active Caddy configuration is unchanged."];
                return RedirectToPage(new { id = routeId });
            }

            var preview = await _applyService.CreatePreviewAsync(
                isNew
                    ? $"Create and activate route {definition.Name}"
                    : $"Update and activate route {definition.Name}",
                actor,
                HttpContext.RequestAborted);
            var result = await _applyService.ApplyAsync(
                preview.Revision.Id,
                actor,
                HttpContext.RequestAborted);
            StatusMessage = _localizer[
                isNew
                    ? "Route created and activated: {0}"
                    : "Route updated and activated: {0}",
                result.Message];
            return RedirectToPage("/Routing/Index");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException)
        {
            if (routeWasSaved && savedRouteId is not null)
            {
                ErrorMessage = _localizer[
                    "The route was saved, but activation failed: {0}",
                    exception.Message];
                return RedirectToPage(new { id = savedRouteId.Value });
            }

            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    private async Task LoadOptionsAsync()
    {
        Domains = await _store.ListDomainsAsync(HttpContext.RequestAborted);
        AccessGroups = (await _store.ListAccessGroupsAsync(HttpContext.RequestAborted))
            .Where(group => group.Enabled)
            .ToArray();
    }

    public sealed class RouteInput
    {
        public Guid? Id { get; set; }

        [Required]
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public Guid DomainId { get; set; }

        [MaxLength(190)]
        public string Subdomain { get; set; } = string.Empty;

        [Required]
        public string Kind { get; set; } = "proxy";

        public bool Enabled { get; set; } = true;

        [Range(-10_000, 10_000)]
        public int SortOrder { get; set; }

        public string CertificateMode { get; set; } = "inherit";

        public Guid? AccessGroupId { get; set; }

        [Required]
        [MaxLength(1024)]
        public string PathPrefix { get; set; } = "/";

        [MaxLength(2048)]
        public string Upstream { get; set; } = string.Empty;

        public bool PreserveHost { get; set; }

        [MaxLength(1024)]
        public string HealthPath { get; set; } = string.Empty;

        [Range(5, 3600)]
        public int HealthIntervalSeconds { get; set; } = 30;

        [MaxLength(4096)]
        public string RedirectTarget { get; set; } = string.Empty;

        public bool RedirectPermanent { get; set; } = true;

        [Range(100, 599)]
        public int StaticStatusCode { get; set; } = 200;

        [MaxLength(64_000)]
        public string StaticBody { get; set; } = string.Empty;

        [MaxLength(64_000)]
        public string CustomSnippet { get; set; } = string.Empty;

        public static RouteInput FromRecord(ManagedRouteRecord record)
        {
            var route = record.Definition;
            return new RouteInput
            {
                Id = route.Id,
                Name = route.Name,
                DomainId = route.DomainId,
                Subdomain = route.Subdomain,
                Kind = ManagedRouteDefinition.ToStorageValue(route.Kind),
                Enabled = route.Enabled,
                SortOrder = route.SortOrder,
                CertificateMode = ManagedRouteDefinition.ToStorageValue(route.CertificateMode),
                AccessGroupId = route.AccessGroupId,
                PathPrefix = route.Configuration.PathPrefix,
                Upstream = route.Configuration.Upstream,
                PreserveHost = route.Configuration.PreserveHost,
                HealthPath = route.Configuration.HealthPath,
                HealthIntervalSeconds = route.Configuration.HealthIntervalSeconds,
                RedirectTarget = route.Configuration.RedirectTarget,
                RedirectPermanent = route.Configuration.RedirectPermanent,
                StaticStatusCode = route.Configuration.StaticStatusCode,
                StaticBody = route.Configuration.StaticBody,
                CustomSnippet = route.Configuration.CustomSnippet,
            };
        }
    }
}
