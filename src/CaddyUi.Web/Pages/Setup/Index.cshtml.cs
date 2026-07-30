using System.ComponentModel.DataAnnotations;
using CaddyUi.Application.Dns;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Operations;
using CaddyUi.Infrastructure.Routing;
using CaddyUi.Infrastructure.Setup;
using CaddyUi.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CaddyUi.Web.Pages.Setup;

[Authorize(Policy = "Administrator")]
public sealed class IndexModel : PageModel
{
    private readonly GuidedSetupService _setupService;
    private readonly DomainProviderStore _domainProviderStore;
    private readonly RoutingOptions _routingOptions;
    private readonly ISecretReferenceProtector _secretProtector;

    public IndexModel(
        GuidedSetupService setupService,
        DomainProviderStore domainProviderStore,
        RoutingOptions routingOptions,
        ISecretReferenceProtector secretProtector)
    {
        _setupService = setupService;
        _domainProviderStore = domainProviderStore;
        _routingOptions = routingOptions;
        _secretProtector = secretProtector;
    }

    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public IReadOnlyList<DnsProviderRecord> ExistingProviders { get; private set; } =
        Array.Empty<DnsProviderRecord>();

    public IReadOnlyList<DnsProviderDefinition> ProviderDefinitions => DnsProviderCatalog.All;

    public bool AllowCustomRoutes => _routingOptions.AllowCustomRoutes;

    public string? LoadError { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var providerSettings = ValuesForProvider(Input.ProviderSettings, Input.ProviderType);
            var providerSecrets = ProtectValuesForProvider(Input.ProviderSecretReferences, Input.ProviderType);
            var result = await _setupService.ProvisionAsync(
                new GuidedSetupRequest(
                    Input.ProviderMode,
                    Input.ExistingProviderId,
                    Input.ProviderType,
                    Input.ProviderLabel,
                    providerSettings,
                    providerSecrets,
                    Input.DomainName,
                    Input.DomainDisplayName,
                    Input.MakeDefaultDomain,
                    Input.RequestWildcardCertificate,
                    Input.RequestBaseCertificate,
                    Input.CreateRoute,
                    Input.RouteName,
                    Input.RouteSubdomain,
                    Input.RouteKind,
                    Input.RoutePathPrefix,
                    Input.RouteCertificateMode,
                    Input.UpstreamScheme,
                    Input.UpstreamHost,
                    Input.UpstreamPort,
                    Input.RedirectTarget,
                    Input.RedirectPermanent,
                    Input.StaticStatusCode,
                    Input.StaticBody,
                    Input.CustomSnippet),
                User.ToManagementActor(HttpContext),
                HttpContext.RequestAborted);

            TempData["SetupMessage"] = result.RouteId is null
                ? $"Domain {result.DomainName} wurde als Entwurf angelegt. Die laufende Caddy-Konfiguration wurde nicht verändert."
                : $"Domain {result.DomainName} und Route {result.RouteHost} wurden als Entwurf angelegt. Erst Vorschau und Apply ändern Caddy.";
            return RedirectToPage(new { completed = true });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Input.ProviderSecretReferences.Clear();
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    public static string FieldName(string providerType, string fieldKey)
    {
        return $"{providerType}.{fieldKey}";
    }

    private async Task LoadAsync()
    {
        try
        {
            ExistingProviders = (await _domainProviderStore.ListProvidersAsync(HttpContext.RequestAborted))
                .Where(provider => provider.Enabled)
                .ToArray();
            LoadError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ExistingProviders = Array.Empty<DnsProviderRecord>();
            LoadError = $"Vorhandene Provider konnten nicht geladen werden: {exception.Message}";
        }
    }

    private IReadOnlyDictionary<string, string> ProtectValuesForProvider(
        IReadOnlyDictionary<string, string>? values,
        string providerType)
    {
        return ValuesForProvider(values, providerType)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => _secretProtector.ProtectOrReference(pair.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> ValuesForProvider(
        IReadOnlyDictionary<string, string>? values,
        string providerType)
    {
        if (values is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var prefix = $"{providerType}.";
        return values
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key[prefix.Length..],
                pair => pair.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SetupInput
    {
        public string ProviderMode { get; set; } = "new";

        public Guid? ExistingProviderId { get; set; }

        public string ProviderType { get; set; } = "netcup";

        [MaxLength(200)]
        public string ProviderLabel { get; set; } = "";

        public Dictionary<string, string> ProviderSettings { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> ProviderSecretReferences { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        [Required]
        [MaxLength(253)]
        public string DomainName { get; set; } = "";

        [MaxLength(200)]
        public string DomainDisplayName { get; set; } = "";

        public bool MakeDefaultDomain { get; set; }

        public bool RequestWildcardCertificate { get; set; } = true;

        public bool RequestBaseCertificate { get; set; } = true;

        public bool CreateRoute { get; set; } = true;

        [MaxLength(120)]
        public string RouteName { get; set; } = "";

        [MaxLength(190)]
        public string RouteSubdomain { get; set; } = "";

        public string RouteKind { get; set; } = "proxy";

        [MaxLength(1024)]
        public string RoutePathPrefix { get; set; } = "/";

        public string RouteCertificateMode { get; set; } = "inherit";

        public string UpstreamScheme { get; set; } = "direct";

        [MaxLength(253)]
        public string UpstreamHost { get; set; } = "";

        [Range(1, 65535)]
        public int? UpstreamPort { get; set; }

        [MaxLength(4096)]
        public string RedirectTarget { get; set; } = "";

        public bool RedirectPermanent { get; set; } = true;

        [Range(100, 599)]
        public int StaticStatusCode { get; set; } = 200;

        [MaxLength(64_000)]
        public string StaticBody { get; set; } = "";

        [MaxLength(64_000)]
        public string CustomSnippet { get; set; } = "";
    }
}
