using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages.Administration;

[Authorize(Policy = "Administrator")]
public sealed class ProviderDnsModel : LocalizedPageModel
{
    private readonly DomainProviderStore _store;
    private readonly DnsProviderRecordQueryService _records;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ProviderDnsModel(
        DomainProviderStore store,
        DnsProviderRecordQueryService records,
        IStringLocalizer<SharedResource> localizer)
    {
        _store = store;
        _records = records;
        _localizer = localizer;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? ProviderId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? DomainId { get; set; }

    public IReadOnlyList<DnsProviderRecord> Providers { get; private set; } =
        Array.Empty<DnsProviderRecord>();

    public IReadOnlyList<ManagedDomainRecord> Domains { get; private set; } =
        Array.Empty<ManagedDomainRecord>();

    public IReadOnlyList<ProviderDnsRecord> DnsRecords { get; private set; } =
        Array.Empty<ProviderDnsRecord>();

    public DnsProviderRecord? SelectedProvider { get; private set; }

    public ManagedDomainRecord? SelectedDomain { get; private set; }

    public DateTimeOffset? LoadedAt { get; private set; }

    public string? LoadError { get; private set; }

    public bool CanListSelectedProvider =>
        SelectedProvider is not null && _records.CanList(SelectedProvider.ProviderType);

    public async Task OnGetAsync()
    {
        try
        {
            Providers = (await _store.ListProvidersAsync(HttpContext.RequestAborted))
                .Where(provider => provider.Enabled)
                .OrderBy(provider => provider.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var allDomains = await _store.ListDomainsAsync(HttpContext.RequestAborted);

            SelectedProvider = ProviderId is null
                ? Providers.FirstOrDefault(provider => _records.CanList(provider.ProviderType)) ?? Providers.FirstOrDefault()
                : Providers.FirstOrDefault(provider => provider.Id == ProviderId.Value);
            ProviderId = SelectedProvider?.Id;

            Domains = SelectedProvider is null
                ? Array.Empty<ManagedDomainRecord>()
                : allDomains
                    .Where(domain =>
                        domain.Enabled &&
                        domain.DnsProviderId == SelectedProvider.Id)
                    .OrderByDescending(domain => domain.IsDefault)
                    .ThenBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            SelectedDomain = DomainId is null
                ? Domains.FirstOrDefault()
                : Domains.FirstOrDefault(domain => domain.Id == DomainId.Value);
            DomainId = SelectedDomain?.Id;

            if (SelectedProvider is null || SelectedDomain is null)
            {
                return;
            }

            if (!_records.CanList(SelectedProvider.ProviderType))
            {
                LoadError = _localizer[
                    "This provider can be configured and tested, but its API does not support record listing in this build."];
                return;
            }

            DnsRecords = await _records.ListAsync(
                SelectedProvider.Id,
                SelectedDomain.Name,
                HttpContext.RequestAborted);
            LoadedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DnsRecords = Array.Empty<ProviderDnsRecord>();
            LoadError = _localizer["DNS records could not be loaded: {0}", exception.Message];
        }
    }
}
