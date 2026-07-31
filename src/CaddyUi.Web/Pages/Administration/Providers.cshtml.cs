using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CaddyUi.Application.Dns;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class ProvidersModel : LocalizedPageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DomainProviderStore _store;
    private readonly DnsProviderRuntimeService _runtime;
    private readonly DnsProviderRecordQueryService _recordQuery;
    private readonly ISecretReferenceProtector _secretProtector;
    private readonly CaddyCertificateSourceRefreshWorker _certificateSources;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ProvidersModel(
        DomainProviderStore store,
        DnsProviderRuntimeService runtime,
        DnsProviderRecordQueryService recordQuery,
        ISecretReferenceProtector secretProtector,
        CaddyCertificateSourceRefreshWorker certificateSources,
        IStringLocalizer<SharedResource> localizer)
    {
        _store = store;
        _runtime = runtime;
        _recordQuery = recordQuery;
        _secretProtector = secretProtector;
        _certificateSources = certificateSources;
        _localizer = localizer;
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

    public bool CanListRecords(string providerType) => _recordQuery.CanList(providerType);

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
                throw new ArgumentException(_localizer["The selected DNS provider is not supported."]);
            var settings = ValuesForProvider(Input.Settings, definition.Type);
            var secrets = ValuesForProvider(Input.Secrets, definition.Type);

            foreach (var field in definition.Settings)
            {
                var value = settings.GetValueOrDefault(field.Key, field.DefaultValue ?? string.Empty).Trim();
                if (field.Required && value.Length == 0)
                {
                    throw new ArgumentException(_localizer["The field '{0}' is required.", field.Label]);
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
                    throw new ArgumentException(_localizer["The field '{0}' is required.", field.Label]);
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
            TempData["Message"] = _localizer[
                "DNS provider saved with encrypted credentials. Assign a domain and run a connection test."];
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
            TempData["Message"] = enabled
                ? _localizer["DNS provider enabled."]
                : _localizer["DNS provider disabled."];
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
            TempData["Message"] = _localizer[
                "DNS-01 timing saved. Review the Caddy preview and apply it."];
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
                throw new InvalidOperationException(_localizer["The selected domain no longer exists."]);
            if (domain.DnsProviderId != providerId)
            {
                throw new InvalidOperationException(_localizer["The domain is not assigned to this provider."]);
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
            LoadError = _localizer["DNS providers could not be loaded: {0}", exception.Message];
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
