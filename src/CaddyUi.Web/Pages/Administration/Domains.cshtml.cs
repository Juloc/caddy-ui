using System.ComponentModel.DataAnnotations;
using CaddyUi.Domain.Certificates;
using CaddyUi.Infrastructure.Management;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class DomainsModel : PageModel
{
    private readonly DomainProviderStore _store;

    public DomainsModel(DomainProviderStore store)
    {
        _store = store;
    }

    public IReadOnlyList<ManagedDomainRecord> Domains { get; private set; } = Array.Empty<ManagedDomainRecord>();

    public IReadOnlyList<DnsProviderRecord> Providers { get; private set; } = Array.Empty<DnsProviderRecord>();

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
            await _store.CreateDomainAsync(
                Input.Name,
                Input.DisplayName,
                Input.DnsProviderId,
                Input.DefaultCertificateMode,
                Input.MakeDefault,
                HttpContext.RequestAborted);
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
    }

    public sealed class DomainInput
    {
        [Required]
        [MaxLength(253)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(200)]
        public string DisplayName { get; set; } = string.Empty;

        public Guid? DnsProviderId { get; set; }

        public CertificateMode DefaultCertificateMode { get; set; } = CertificateMode.Wildcard;

        public bool MakeDefault { get; set; }
    }
}
