using System.ComponentModel.DataAnnotations;
using CaddyUi.Infrastructure.Management;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class ProvidersModel : PageModel
{
    private readonly DomainProviderStore _store;

    public ProvidersModel(DomainProviderStore store)
    {
        _store = store;
    }

    public IReadOnlyList<DnsProviderRecord> Providers { get; private set; } = Array.Empty<DnsProviderRecord>();

    [BindProperty]
    public ProviderInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        Providers = await _store.ListProvidersAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            Providers = await _store.ListProvidersAsync(HttpContext.RequestAborted);
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
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Providers = await _store.ListProvidersAsync(HttpContext.RequestAborted);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid providerId, bool enabled)
    {
        await _store.SetProviderEnabledAsync(providerId, enabled, HttpContext.RequestAborted);
        return RedirectToPage();
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
