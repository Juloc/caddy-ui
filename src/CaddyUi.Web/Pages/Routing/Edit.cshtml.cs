using System.ComponentModel.DataAnnotations;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Routing;

[Authorize(Policy = "Editor")]
public sealed class EditModel : PageModel
{
    private readonly RouteManagementStore _store;
    private readonly CaddyApplyService _applyService;

    public EditModel(RouteManagementStore store, CaddyApplyService applyService)
    {
        _store = store;
        _applyService = applyService;
    }

    [BindProperty]
    public RouteInput Input { get; set; } = new();

    public IReadOnlyList<ManagedDomainOption> Domains { get; private set; } =
        Array.Empty<ManagedDomainOption>();

    public IReadOnlyList<AccessGroupRecord> AccessGroups { get; private set; } =
        Array.Empty<AccessGroupRecord>();

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

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadOptionsAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var domain = Domains.FirstOrDefault(item => item.Id == Input.DomainId && item.Enabled) ??
                throw new InvalidOperationException("Die ausgewählte Domain existiert nicht oder ist deaktiviert.");
            var kind = ManagedRouteDefinition.ParseKind(Input.Kind);
            if (kind == ManagedRouteKind.Custom && !AllowCustomRoutes)
            {
                throw new InvalidOperationException("Benutzerdefinierte Caddy-Routen sind in der Konfiguration deaktiviert.");
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
            var definition = ManagedRouteDefinition.Create(
                Input.Id ?? Guid.NewGuid(),
                Input.Name,
                domain.Id,
                domain.Name,
                Input.Subdomain,
                kind,
                Input.Enabled,
                Input.SortOrder,
                ManagedRouteDefinition.ParseCertificateMode(Input.CertificateMode),
                Input.AccessGroupId,
                configuration);
            var actor = User.ToManagementActor(HttpContext);
            if (Input.Id is null)
            {
                await _store.CreateRouteAsync(definition, actor, HttpContext.RequestAborted);
            }
            else
            {
                await _store.UpdateRouteAsync(definition, actor, HttpContext.RequestAborted);
            }

            TempData["StatusMessage"] = Input.Id is null
                ? "Route angelegt. Vor einer Aktivierung muss eine Vorschau erstellt und angewendet werden."
                : "Route gespeichert. Die aktive Caddy-Konfiguration bleibt bis zum Apply unverändert.";
            return RedirectToPage("/Routing/Index");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
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
