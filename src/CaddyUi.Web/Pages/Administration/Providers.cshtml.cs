using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CaddyUi.Application.Dns;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class ProvidersModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DomainProviderStore _store;
    private readonly DnsProviderRuntimeService _runtime;
    private readonly ISecretReferenceProtector _secretProtector;
    private readonly CaddyCertificateSourceRefreshWorker _certificateSources;

    public ProvidersModel(
        DomainProviderStore store,
        DnsProviderRuntimeService runtime,
        ISecretReferenceProtector secretProtector,
        CaddyCertificateSourceRefreshWorker certificateSources)
    {
        _store = store;
        _runtime = runtime;
        _secretProtector = secretProtector;
        _certificateSources = certificateSources;
    }

    public IReadOnlyList<DnsProviderRecord> Providers { get; private set; } = Array.Empty<DnsProviderRecord>();
    public IReadOnlyList<ManagedDomainRecord> Domains { get; private set; } = Array.Empty<ManagedDomainRecord>();
    public string? LoadError { get; private set; }

    [BindProperty]
    public ProviderInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public bool SupportsDirectApi(string providerType) => _runtime.IsRuntimeSupported(providerType);

    public bool HasCaddyDnsModule(string providerType) => _runtime.IsCaddyDnsModuleInstalled(providerType);

    public static bool SupportsManagedDnsChallengeTiming(string providerType)
    {
        return string.Equals(providerType, "netcup", StringComparison.OrdinalIgnoreCase);
    }

    public static string FieldName(string providerType, string fieldKey) => $"{providerType}.{fieldKey}";

    public static string ProviderSetting(DnsProviderRecord provider, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(provider.ConfigJson);
            if (!document.RootElement.TryGetProperty(key, out var value))
            {
                return string.Empty;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : value.GetRawText();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
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
            var definition = DnsProviderCatalog.Find(Input.ProviderType) ??
                throw new ArgumentException("Der ausgewählte DNS-Provider wird nicht unterstützt.");
            var settings = ValuesForProvider(Input.Settings, definition.Type);
            var secrets = ValuesForProvider(Input.Secrets, definition.Type);

            foreach (var field in definition.Settings)
            {
                var value = settings.GetValueOrDefault(field.Key, field.DefaultValue ?? string.Empty).Trim();
                if (field.Required && value.Length == 0)
                {
                    throw new ArgumentException($"Das Feld '{field.Label}' ist erforderlich.");
                }

                if (value.Length > 0)
                {
                    settings[field.Key] = value;
                }
            }

            foreach (var field in definition.Secrets)
            {
                var value = secrets.GetValueOrDefault(field.Key, string.Empty).Trim();
                if (field.Required && value.Length == 0)
                {
                    throw new ArgumentException($"Das Feld '{field.Label}' ist erforderlich.");
                }

                if (value.Length > 0)
                {
                    secrets[field.Key] = _secretProtector.ProtectOrReference(value);
                }
            }

            await _store.CreateProviderAsync(
                definition.Type,
                Input.Label,
                JsonSerializer.Serialize(settings, JsonOptions),
                JsonSerializer.Serialize(secrets, JsonOptions),
                HttpContext.RequestAborted);
            await _certificateSources.RefreshAsync(HttpContext.RequestAborted);
            TempData["Message"] = "DNS-Provider wurde verschlüsselt gespeichert. Ordne jetzt eine Domain zu und führe einen Verbindungstest aus.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Input.Secrets.Clear();
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid providerId, bool enabled)
    {
        try
        {
            await _store.SetProviderEnabledAsync(providerId, enabled, HttpContext.RequestAborted);
            await _certificateSources.RefreshAsync(HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDnsChallengeTimingAsync(
        Guid providerId,
        string? propagationDelay,
        string? propagationTimeout)
    {
        try
        {
            await _store.UpdateDnsChallengeTimingAsync(
                providerId,
                propagationDelay,
                propagationTimeout,
                HttpContext.RequestAborted);
            await _certificateSources.RefreshAsync(HttpContext.RequestAborted);
            TempData["Message"] = "DNS-01-Zeiten wurden gespeichert. Prüfe anschließend die Caddy-Vorschau und wende sie an.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(Guid providerId, Guid domainId)
    {
        try
        {
            var domain = (await _store.ListDomainsAsync(HttpContext.RequestAborted))
                .FirstOrDefault(item => item.Id == domainId) ??
                throw new InvalidOperationException("Die ausgewählte Domain existiert nicht mehr.");
            if (domain.DnsProviderId != providerId)
            {
                throw new InvalidOperationException("Die Domain ist diesem Provider nicht zugeordnet.");
            }

            var result = await _runtime.TestProviderAsync(providerId, domain.Name, HttpContext.RequestAborted);
            TempData[result.Succeeded ? "Message" : "Error"] = result.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        try
        {
            Providers = await _store.ListProvidersAsync(HttpContext.RequestAborted);
            Domains = await _store.ListDomainsAsync(HttpContext.RequestAborted);
            LoadError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Providers = Array.Empty<DnsProviderRecord>();
            Domains = Array.Empty<ManagedDomainRecord>();
            LoadError = $"DNS-Provider konnten nicht geladen werden: {exception.Message}";
        }
    }

    private static Dictionary<string, string> ValuesForProvider(
        IReadOnlyDictionary<string, string>? values,
        string providerType)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return result;
        }

        var prefix = $"{providerType}.";
        foreach (var pair in values.Where(pair =>
                     pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            result[pair.Key[prefix.Length..]] = pair.Value ?? string.Empty;
        }

        return result;
    }

    public sealed class ProviderInput
    {
        [Required]
        [MaxLength(100)]
        public string ProviderType { get; set; } = "netcup";

        [Required]
        [MaxLength(200)]
        public string Label { get; set; } = string.Empty;

        public Dictionary<string, string> Settings { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Secrets { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
