using System.ComponentModel.DataAnnotations;
using CaddyUi.Infrastructure.Management;
using CaddyUi.Infrastructure.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace CaddyUi.Web.Pages.Operations;

[Authorize(Policy = "Administrator")]
public sealed class DnsModel : LocalizedPageModel
{
    private readonly OperationsStore _store;
    private readonly DdnsProvisioningStore _provisioning;
    private readonly DomainProviderStore _management;
    private readonly DnsProviderRuntimeService _providers;
    private readonly DdnsService _ddns;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public DnsModel(
        OperationsStore store,
        DdnsProvisioningStore provisioning,
        DomainProviderStore management,
        DnsProviderRuntimeService providers,
        DdnsService ddns,
        IStringLocalizer<SharedResource> localizer)
    {
        _store = store;
        _provisioning = provisioning;
        _management = management;
        _providers = providers;
        _ddns = ddns;
        _localizer = localizer;
    }

    public IReadOnlyList<ManagedDnsRecord> Records { get; private set; } = Array.Empty<ManagedDnsRecord>();
    public IReadOnlyList<DdnsTargetRecord> DdnsTargets { get; private set; } = Array.Empty<DdnsTargetRecord>();
    public IReadOnlyList<ManagedDomainRecord> Domains { get; private set; } = Array.Empty<ManagedDomainRecord>();
    public IReadOnlyList<DnsProviderRecord> Providers { get; private set; } = Array.Empty<DnsProviderRecord>();

    [BindProperty]
    public DnsRecordInput RecordInput { get; set; } = new();

    [BindProperty]
    public DdnsInput DynamicInput { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateRecordAsync()
    {
        RemoveModelStatePrefix(nameof(DynamicInput));
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            await _store.CreateDnsRecordAsync(
                RecordInput.DomainId,
                RecordInput.ProviderId,
                RecordInput.Name,
                RecordInput.RecordType,
                RecordInput.Value,
                RecordInput.Ttl,
                RecordInput.Priority,
                HttpContext.RequestAborted);
            TempData["Message"] = _localizer["DNS record created as a managed draft."];
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleRecordAsync(Guid recordId, bool enabled)
    {
        await _store.SetDnsRecordEnabledAsync(recordId, enabled, HttpContext.RequestAborted);
        TempData["Message"] = enabled
            ? _localizer["DNS record enabled."]
            : _localizer["DNS record disabled."];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSyncRecordAsync(Guid recordId)
    {
        var record = (await _store.ListDnsRecordsAsync(HttpContext.RequestAborted))
            .FirstOrDefault(item => item.Id == recordId) ??
            throw new InvalidOperationException(_localizer["The DNS record no longer exists."]);
        ProviderOperationResult result;
        try
        {
            result = await _providers.UpsertRecordAsync(
                record.ProviderId,
                new DnsRecordMutation(
                    record.DomainName,
                    record.Name,
                    record.RecordType,
                    record.Value,
                    record.Ttl,
                    record.Priority),
                HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            result = ProviderOperationResult.Failure(exception.Message);
        }

        await _store.MarkDnsRecordSyncAsync(recordId, result, HttpContext.RequestAborted);
        TempData[result.Succeeded ? "Message" : "Error"] = result.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateDdnsAsync()
    {
        RemoveModelStatePrefix(nameof(RecordInput));
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        try
        {
            var domains = await _management.ListDomainsAsync(HttpContext.RequestAborted);
            var domain = domains.FirstOrDefault(item => item.Id == DynamicInput.DomainId) ??
                throw new InvalidOperationException(_localizer["The selected domain no longer exists."]);
            if (!domain.Enabled)
            {
                throw new InvalidOperationException(_localizer["The selected domain is disabled."]);
            }

            var providerId = domain.DnsProviderId ??
                throw new InvalidOperationException(
                    _localizer["Assign an enabled DNS provider to the domain before creating DDNS."]);

            var targetIds = await _provisioning.UpsertTargetsAsync(
                domain.Id,
                providerId,
                [new DdnsTargetConfiguration(
                    DynamicInput.Name,
                    DynamicInput.RecordType,
                    DynamicInput.AddressSource,
                    DynamicInput.StaticValue,
                    true)],
                DynamicInput.IntervalSeconds,
                HttpContext.RequestAborted);

            var targetId = targetIds.Single();
            var target = (await _store.ListDdnsTargetsAsync(HttpContext.RequestAborted))
                .First(item => item.Id == targetId);
            var result = await _ddns.RunAsync(target, HttpContext.RequestAborted);
            TempData[result.Succeeded ? "Message" : "Error"] = result.Succeeded
                ? _localizer["DDNS target saved and synchronized. {0}", result.Message]
                : _localizer["DDNS target was saved, but the initial synchronization failed: {0}", result.Message];
            return RedirectToPage("/Operations/Dns", null, null, "ddns-targets");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleDdnsAsync(Guid targetId, bool enabled)
    {
        await _store.SetDdnsTargetEnabledAsync(targetId, enabled, HttpContext.RequestAborted);
        TempData["Message"] = enabled
            ? _localizer["DDNS target enabled."]
            : _localizer["DDNS target disabled."];
        return RedirectToPage("/Operations/Dns", null, null, "ddns-targets");
    }

    public async Task<IActionResult> OnPostRunDdnsAsync(Guid targetId)
    {
        var target = (await _store.ListDdnsTargetsAsync(HttpContext.RequestAborted))
            .FirstOrDefault(item => item.Id == targetId) ??
            throw new InvalidOperationException(_localizer["The DDNS target no longer exists."]);
        var result = await _ddns.RunAsync(target, HttpContext.RequestAborted);
        TempData[result.Succeeded ? "Message" : "Error"] = result.Message;
        return RedirectToPage("/Operations/Dns", null, null, "ddns-targets");
    }

    private void RemoveModelStatePrefix(string prefix)
    {
        foreach (var key in ModelState.Keys
                     .Where(key => key.StartsWith(prefix + ".", StringComparison.Ordinal))
                     .ToArray())
        {
            ModelState.Remove(key);
        }
    }

    private async Task LoadAsync()
    {
        Records = await _store.ListDnsRecordsAsync(HttpContext.RequestAborted);
        DdnsTargets = await _store.ListDdnsTargetsAsync(HttpContext.RequestAborted);
        Domains = await _management.ListDomainsAsync(HttpContext.RequestAborted);
        Providers = await _management.ListProvidersAsync(HttpContext.RequestAborted);
    }

    public sealed class DnsRecordInput
    {
        [Required]
        public Guid DomainId { get; set; }

        [Required]
        public Guid ProviderId { get; set; }

        [MaxLength(253)]
        public string Name { get; set; } = "@";

        [Required]
        [MaxLength(16)]
        public string RecordType { get; set; } = "A";

        [Required]
        [MaxLength(4000)]
        public string Value { get; set; } = string.Empty;

        [Range(30, 86400)]
        public int Ttl { get; set; } = 300;

        public int? Priority { get; set; }
    }

    public sealed class DdnsInput
    {
        [Required]
        public Guid DomainId { get; set; }

        [MaxLength(253)]
        public string Name { get; set; } = "@";

        [Required]
        public string RecordType { get; set; } = "A";

        [Range(60, 86400)]
        public int IntervalSeconds { get; set; } = 300;

        [Required]
        public string AddressSource { get; set; } = "public";

        [MaxLength(128)]
        public string StaticValue { get; set; } = string.Empty;
    }
}
