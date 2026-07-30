using System.ComponentModel.DataAnnotations;
using CaddyUi.Infrastructure.Certificates;
using CaddyUi.Infrastructure.Management;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class DomainsModel : PageModel
{
    private readonly DomainProviderStore _store;
    private readonly CertificateStatusService _certificateStatusService;

    public DomainsModel(
        DomainProviderStore store,
        CertificateStatusService certificateStatusService)
    {
        _store = store;
        _certificateStatusService = certificateStatusService;
    }

    public IReadOnlyList<ManagedDomainRecord> Domains { get; private set; } = Array.Empty<ManagedDomainRecord>();

    public IReadOnlyList<DnsProviderRecord> Providers { get; private set; } = Array.Empty<DnsProviderRecord>();

    public IReadOnlyDictionary<Guid, DomainCertificateStatus> CertificateStatuses { get; private set; } =
        new Dictionary<Guid, DomainCertificateStatus>();

    [BindProperty]
    public DomainInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            await _store.CreateDomainWithCertificatePlanAsync(
                Input.Name,
                Input.DisplayName,
                Input.DnsProviderId,
                Input.RequestWildcardCertificate,
                Input.RequestBaseCertificate,
                Input.MakeDefault,
                HttpContext.RequestAborted);
            TempData["Message"] = "Domain wurde als Entwurf angelegt. Zertifikate werden erst nach Vorschau und Apply beschafft.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDefaultAsync(Guid domainId)
    {
        await _store.SetDefaultDomainAsync(domainId, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Domains = await _store.ListDomainsAsync(HttpContext.RequestAborted);
        Providers = (await _store.ListProvidersAsync(HttpContext.RequestAborted))
            .Where(provider => provider.Enabled)
            .ToArray();
        CertificateStatuses = await _certificateStatusService.GetDomainStatusesAsync(HttpContext.RequestAborted);
    }

    public static string StatusClass(string state)
    {
        return state switch
        {
            "active" => "status-badge--ok",
            "renewal-due" or "requested" or "draft" => "status-badge--warning",
            "blocked" or "expired" => "status-badge--danger",
            _ => "status-badge--neutral",
        };
    }

    public sealed class DomainInput
    {
        [Required]
        [MaxLength(253)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        public Guid? DnsProviderId { get; set; }

        public bool RequestWildcardCertificate { get; set; } = true;

        public bool RequestBaseCertificate { get; set; } = true;

        public bool MakeDefault { get; set; }
    }
}
