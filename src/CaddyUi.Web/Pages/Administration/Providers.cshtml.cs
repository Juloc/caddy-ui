using System.ComponentModel.DataAnnotations;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class ProvidersModel : PageModel
{
    private readonly DomainProviderStore _store;
    private readonly DnsProviderRuntimeService _runtime;

    public ProvidersModel(DomainProviderStore store, DnsProviderRuntimeService runtime)
    {
        _store = store;
        _runtime = runtime;
    }

    public IReadOnlyList<DnsProviderRecord> Providers { get; private set; } = Array.Empty<DnsProviderRecord>();
    public IReadOnlyList<ManagedDomainRecord> Domains { get; private set; } = Array.Empty<ManagedDomainRecord>();

    [BindProperty]
    public ProviderInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public bool SupportsDirectApi(string providerType) => _runtime.IsRuntimeSupported(providerType);

    public bool HasCaddyDnsModule(string providerType) => _runtime.IsCaddyDnsModuleInstalled(providerType);

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            await _store.CreateProviderAsync(
                Input.ProviderType,
                Input.Label,
                Input.ConfigJson,
                Input.SecretReferencesJson,
                HttpContext.RequestAborted);
            TempData["Message"] = "DNS-Provider wurde angelegt.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid providerId, bool enabled)
    {
        await _store.SetProviderEnabledAsync(providerId, enabled, HttpContext.RequestAborted);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(Guid providerId, Guid domainId)
    {
        var domain = (await _store.ListDomainsAsync(HttpContext.RequestAborted))
            .FirstOrDefault(item => item.Id == domainId) ??
            throw new InvalidOperationException("Die ausgewählte Domain existiert nicht mehr.");
        if (domain.DnsProviderId != providerId)
        {
            TempData["Error"] = "Die Domain ist diesem Provider nicht zugeordnet.";
            return RedirectToPage();
        }

        var result = await _runtime.TestProviderAsync(providerId, domain.Name, HttpContext.RequestAborted);
        TempData[result.Succeeded ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Providers = await _store.ListProvidersAsync(HttpContext.RequestAborted);
        Domains = await _store.ListDomainsAsync(HttpContext.RequestAborted);
    }

    public sealed class ProviderInput
    {
        [Required]
        [MaxLength(100)]
        public string ProviderType { get; set; } = "netcup";

        [Required]
        [MaxLength(200)]
        public string Label { get; set; } = string.Empty;

        [Required]
        public string ConfigJson { get; set; } = "{}";

        [Required]
        public string SecretReferencesJson { get; set; } = "{}";
    }
}
