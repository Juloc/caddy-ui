using System.Net;
using System.Net.Sockets;

namespace CaddyUi.Infrastructure.Operations;

public sealed class PublicIpAddressResolver
{
    private readonly OperationsOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public PublicIpAddressResolver(OperationsOptions options, IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> ResolveAsync(string recordType, CancellationToken cancellationToken = default)
    {
        var family = recordType == "AAAA" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        var services = recordType == "AAAA" ? _options.PublicIpv6Services : _options.PublicIpv4Services;
        var errors = new List<string>();
        foreach (var service in services)
        {
            try
            {
                using var response = await _httpClientFactory.CreateClient("public-ip")
                    .GetAsync(service, cancellationToken);
                var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (response.IsSuccessStatusCode && IPAddress.TryParse(body, out var address) && address.AddressFamily == family)
                {
                    return address.ToString();
                }

                errors.Add($"{service.Host}: invalid response");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{service.Host}: {exception.Message}");
            }
        }

        throw new InvalidOperationException($"No public {recordType} address service returned a valid address. {string.Join("; ", errors)}");
    }
}

public sealed class DdnsService
{
    private readonly OperationsStore _store;
    private readonly DnsProviderRuntimeService _providers;
    private readonly PublicIpAddressResolver _addresses;
    private readonly NotificationDispatcher _notifications;

    public DdnsService(
        OperationsStore store,
        DnsProviderRuntimeService providers,
        PublicIpAddressResolver addresses,
        NotificationDispatcher notifications)
    {
        _store = store;
        _providers = providers;
        _addresses = addresses;
        _notifications = notifications;
    }

    public async Task<ProviderOperationResult> RunAsync(DdnsTargetRecord target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        string value;
        try
        {
            value = target.AddressSource == "static"
                ? target.StaticValue
                : await _addresses.ResolveAsync(target.RecordType, cancellationToken);
            if (string.Equals(value, target.LastValue, StringComparison.Ordinal))
            {
                var unchanged = ProviderOperationResult.Success($"{target.Fqdn} is unchanged at {value}.");
                await _store.CompleteDdnsTargetAsync(target.Id, value, unchanged, cancellationToken);
                return unchanged;
            }

            var result = await _providers.UpsertRecordAsync(
                target.ProviderId,
                new DnsRecordMutation(target.DomainName, target.Name, target.RecordType, value, 300),
                cancellationToken);
            var persistedValue = IsShadowResult(result) ? target.LastValue : value;
            await _store.CompleteDdnsTargetAsync(target.Id, persistedValue, result, cancellationToken);
            if (!result.Succeeded)
            {
                await NotifyFailureAsync(target, result, cancellationToken);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var result = ProviderOperationResult.Failure(exception.Message);
            await _store.CompleteDdnsTargetAsync(target.Id, target.LastValue, result, cancellationToken);
            await NotifyFailureAsync(target, result, cancellationToken);
            return result;
        }
    }

    public async Task<ProviderOperationResult> RunAllAsync(CancellationToken cancellationToken = default)
    {
        var targets = await _store.ListDdnsTargetsAsync(cancellationToken);
        var enabled = targets.Where(target => target.Enabled).ToArray();
        var failures = new List<string>();
        foreach (var target in enabled)
        {
            var result = await RunAsync(target, cancellationToken);
            if (!result.Succeeded)
            {
                failures.Add($"{target.Fqdn}: {result.Message}");
            }
        }

        return failures.Count == 0
            ? ProviderOperationResult.Success($"Processed {enabled.Length} DDNS targets.")
            : ProviderOperationResult.Failure(string.Join(" ", failures));
    }

    private static bool IsShadowResult(ProviderOperationResult result)
    {
        return result.Succeeded && result.Message.StartsWith("Shadow:", StringComparison.Ordinal);
    }

    private Task NotifyFailureAsync(DdnsTargetRecord target, ProviderOperationResult result, CancellationToken cancellationToken)
    {
        return _notifications.NotifyAsync(
            new SystemNotification(
                "error",
                "ddns.failed",
                $"DDNS fehlgeschlagen: {target.Fqdn}",
                result.Message,
                "ddns_target",
                target.Id.ToString("D")),
            cancellationToken);
    }
}
