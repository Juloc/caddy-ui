using System.ComponentModel.DataAnnotations;
using CaddyUi.Domain.Routing;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages.StaticSites;

[Authorize(Policy = "Editor")]
public sealed class EditModel : LocalizedPageModel
{
    private readonly RouteManagementStore _store;
    private readonly CaddyApplyService _applyService;
    private readonly IStringLocalizer<StaticSitesResource> _localizer;
    private readonly ILogger<EditModel> _logger;

    public EditModel(
        RouteManagementStore store,
        CaddyApplyService applyService,
        IStringLocalizer<StaticSitesResource> localizer,
        ILogger<EditModel> logger)
    {
        _store = store;
        _applyService = applyService;
        _localizer = localizer;
        _logger = logger;
    }

    [BindProperty]
    public StaticSiteInput Input { get; set; } = new();

    public IReadOnlyList<ManagedDomainOption> Domains { get; private set; } =
        Array.Empty<ManagedDomainOption>();

    public bool IsEdit => Input.Id is not null;

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id)
    {
        await LoadDomainsAsync();
        if (id is null)
        {
            var domain = Domains.FirstOrDefault(item => item.IsDefault && item.Enabled) ??
                         Domains.FirstOrDefault(item => item.Enabled);
            Input.DomainId = domain?.Id ?? Guid.Empty;
            return Page();
        }

        var existing = await _store.GetRouteAsync(id.Value, HttpContext.RequestAborted);
        if (existing is null || existing.Definition.Kind != ManagedRouteKind.StaticSite)
        {
            return NotFound();
        }

        Input = StaticSiteInput.FromRecord(existing);
        return Page();
    }

    public Task<IActionResult> OnPostAsync()
    {
        return SaveAsync(applyAfterSave: false);
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
        await LoadDomainsAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var routeSaved = false;
        Guid? savedRouteId = null;
        try
        {
            var domain = Domains.FirstOrDefault(item => item.Id == Input.DomainId && item.Enabled) ??
                throw new InvalidOperationException(_localizer["The selected domain does not exist or is disabled."]);

            ManagedRouteRecord? existing = null;
            if (Input.Id is Guid existingId)
            {
                existing = await _store.GetRouteAsync(existingId, HttpContext.RequestAborted);
                if (existing is null || existing.Definition.Kind != ManagedRouteKind.StaticSite)
                {
                    return NotFound();
                }
            }

            var routeId = Input.Id ?? Guid.NewGuid();
            var configuration = RouteConfigurationDocument.Empty with
            {
                StaticSiteTitle = Input.Title,
                StaticSiteContact = Input.Contact,
                StaticSiteHomeText = Input.HomeText,
                StaticSitePrivacyText = Input.PrivacyText,
                StaticSiteTermsText = Input.TermsText,
            };
            var definition = ManagedRouteDefinition.Create(
                routeId,
                existing?.Definition.Name ?? $"Mini-site {routeId:N}",
                domain.Id,
                domain.Name,
                Input.Subdomain,
                ManagedRouteKind.StaticSite,
                true,
                0,
                ManagedRouteDefinition.ParseCertificateMode(Input.CertificateMode),
                null,
                configuration);
            var actor = User.ToManagementActor(HttpContext);
            var isNew = existing is null;
            if (isNew)
            {
                await _store.CreateRouteAsync(definition, actor, HttpContext.RequestAborted);
            }
            else
            {
                await _store.UpdateRouteAsync(definition, actor, HttpContext.RequestAborted);
            }

            routeSaved = true;
            savedRouteId = routeId;
            Input.Id = routeId;

            if (!applyAfterSave)
            {
                StatusMessage = isNew
                    ? _localizer["Mini site saved. Caddy is unchanged until you apply the saved configuration."]
                    : _localizer["Mini site updated. Caddy is unchanged until you apply the saved configuration."];
                return RedirectToPage(new { id = routeId });
            }

            var preview = await _applyService.CreatePreviewAsync(
                isNew
                    ? $"Create and activate static site {definition.Host}"
                    : $"Update and activate static site {definition.Host}",
                actor,
                HttpContext.RequestAborted);
            var result = await _applyService.ApplyAsync(
                preview.Revision.Id,
                actor,
                HttpContext.RequestAborted);
            StatusMessage = _localizer["Mini site saved and activated: {0}", result.Message];
            return RedirectToPage("/StaticSites/Index");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Static site save or activation failed for {RouteId}.", Input.Id);
            if (routeSaved && savedRouteId is not null)
            {
                ErrorMessage = _localizer["The mini site was saved, but activation failed: {0}", exception.Message];
                return RedirectToPage(new { id = savedRouteId.Value });
            }

            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    private async Task LoadDomainsAsync()
    {
        Domains = await _store.ListDomainsAsync(HttpContext.RequestAborted);
    }

    public sealed class StaticSiteInput
    {
        public Guid? Id { get; set; }

        [Required]
        public Guid DomainId { get; set; }

        [DisplayFormat(ConvertEmptyStringToNull = false)]
        [MaxLength(190)]
        public string Subdomain { get; set; } = string.Empty;

        [Required]
        [MaxLength(160)]
        public string Title { get; set; } = string.Empty;

        [DisplayFormat(ConvertEmptyStringToNull = false)]
        [MaxLength(320)]
        [EmailAddress]
        public string Contact { get; set; } = string.Empty;

        [Required]
        [MaxLength(6_000)]
        public string HomeText { get; set; } = string.Empty;

        [Required]
        [MaxLength(6_000)]
        public string PrivacyText { get; set; } = string.Empty;

        [Required]
        [MaxLength(6_000)]
        public string TermsText { get; set; } = string.Empty;

        [Required]
        public string CertificateMode { get; set; } = "inherit";

        public static StaticSiteInput FromRecord(ManagedRouteRecord record)
        {
            var route = record.Definition;
            return new StaticSiteInput
            {
                Id = route.Id,
                DomainId = route.DomainId,
                Subdomain = route.Subdomain,
                Title = route.Configuration.StaticSiteTitle,
                Contact = route.Configuration.StaticSiteContact,
                HomeText = route.Configuration.StaticSiteHomeText,
                PrivacyText = route.Configuration.StaticSitePrivacyText,
                TermsText = route.Configuration.StaticSiteTermsText,
                CertificateMode = ManagedRouteDefinition.ToStorageValue(route.CertificateMode),
            };
        }
    }
}
