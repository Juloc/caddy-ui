using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CaddyUi.Infrastructure.Operations;

public interface IDnsProviderAdapter
{
    bool Supports(string providerType);

    Task<ProviderOperationResult> TestAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken = default);

    Task<ProviderOperationResult> UpsertAsync(
        DnsProviderContext context,
        DnsRecordMutation mutation,
        CancellationToken cancellationToken = default);
}

public sealed class DnsProviderRuntimeService
{
    private readonly OperationsStore _store;
    private readonly ISecretReferenceResolver _secrets;
    private readonly IReadOnlyList<IDnsProviderAdapter> _adapters;
    private readonly OperationsOptions _options;

    public DnsProviderRuntimeService(
        OperationsStore store,
        ISecretReferenceResolver secrets,
        IEnumerable<IDnsProviderAdapter> adapters,
        OperationsOptions options)
    {
        _store = store;
        _secrets = secrets;
        _adapters = adapters.ToArray();
        _options = options;
    }

    public bool IsRuntimeSupported(string providerType)
    {
        return _adapters.Any(adapter => adapter.Supports(providerType));
    }

    public bool IsCaddyDnsModuleInstalled(string providerType)
    {
        return _options.InstalledCaddyDnsModules.Contains(providerType);
    }

    public async Task<ProviderOperationResult> TestProviderAsync(
        Guid providerId,
        string domain,
        CancellationToken cancellationToken = default)
    {
        var provider = await _store.GetProviderAsync(providerId, cancellationToken) ??
            throw new InvalidOperationException("The DNS provider does not exist.");
        if (!provider.Enabled)
        {
            throw new InvalidOperationException("The DNS provider is disabled.");
        }

        ProviderOperationResult result;
        try
        {
            var context = await CreateContextAsync(provider, cancellationToken);
            var adapter = FindAdapter(provider.ProviderType);
            result = await adapter.TestAsync(context, NormalizeDomain(domain), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = ProviderOperationResult.Failure(Limit(exception.Message, 1000));
        }

        await _store.RecordProviderTestAsync(providerId, result, cancellationToken);
        return result;
    }

    public async Task<ProviderOperationResult> UpsertRecordAsync(
        Guid providerId,
        DnsRecordMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateMutation(mutation);
        var provider = await _store.GetProviderAsync(providerId, cancellationToken) ??
            throw new InvalidOperationException("The DNS provider does not exist.");
        if (!provider.Enabled)
        {
            throw new InvalidOperationException("The DNS provider is disabled.");
        }

        if (_options.DnsWriteMode == OperationsWriteMode.Disabled)
        {
            throw new InvalidOperationException("DNS writes are disabled. Set Operations:DnsWriteMode to shadow or active after validating the provider configuration.");
        }

        if (_options.DnsWriteMode == OperationsWriteMode.Shadow)
        {
            return ProviderOperationResult.Success(
                $"Shadow: {mutation.RecordType} {mutation.Fqdn} would be set to {mutation.Value}.");
        }

        var context = await CreateContextAsync(provider, cancellationToken);
        return await FindAdapter(provider.ProviderType).UpsertAsync(context, mutation, cancellationToken);
    }

    private async Task<DnsProviderContext> CreateContextAsync(
        DnsProviderRuntimeRecord provider,
        CancellationToken cancellationToken)
    {
        var references = OperationsJson.ReadStringObject(provider.SecretReferencesJson);
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in references)
        {
            resolved[pair.Key] = await _secrets.ResolveAsync(pair.Value, cancellationToken);
        }

        return new DnsProviderContext(provider, OperationsJson.ReadStringObject(provider.ConfigJson), resolved);
    }

    private IDnsProviderAdapter FindAdapter(string providerType)
    {
        return _adapters.FirstOrDefault(adapter => adapter.Supports(providerType)) ??
            throw new InvalidOperationException(
                $"Provider '{providerType}' is available in management, but this build has no direct record API adapter. Use its external Caddy DNS module or add a runtime adapter before enabling writes.");
    }

    private static string NormalizeDomain(string domain)
    {
        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Contains('/') || normalized.Contains(' '))
        {
            throw new ArgumentException("A valid managed domain is required.", nameof(domain));
        }

        return normalized;
    }

    private static void ValidateMutation(DnsRecordMutation mutation)
    {
        _ = NormalizeDomain(mutation.Domain);
        if (mutation.Name.Contains('\r') || mutation.Name.Contains('\n') || mutation.Name.Contains(' '))
        {
            throw new ArgumentException("The DNS record name is invalid.", nameof(mutation));
        }

        if (mutation.RecordType is not ("A" or "AAAA" or "CNAME" or "TXT" or "MX" or "CAA" or "SRV"))
        {
            throw new ArgumentException("The DNS record type is not supported.", nameof(mutation));
        }

        if (string.IsNullOrWhiteSpace(mutation.Value) || mutation.Value.Contains('\r') || mutation.Value.Contains('\n'))
        {
            throw new ArgumentException("The DNS record value is invalid.", nameof(mutation));
        }

        if (mutation.Ttl is < 30 or > 86400)
        {
            throw new ArgumentOutOfRangeException(nameof(mutation), "TTL must be between 30 and 86400 seconds.");
        }
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}

public abstract class HttpDnsProviderAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;

    protected HttpDnsProviderAdapter(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    protected HttpClient CreateClient() => _httpClientFactory.CreateClient("dns-providers");

    protected static StringContent JsonContent(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");
    }

    protected static async Task<JsonDocument> SendJsonAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Provider API returned HTTP {(int)response.StatusCode}: {Limit(body, 600)}");
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    protected static HttpRequestMessage Bearer(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    protected static string RelativeName(DnsRecordMutation mutation)
    {
        return mutation.Name is "" or "@" ? "@" : mutation.Name.TrimEnd('.');
    }

    protected static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}

public sealed class NetcupDnsProviderAdapter : HttpDnsProviderAdapter, IDnsProviderAdapter
{
    private const string DefaultEndpoint = "https://ccp.netcup.net/run/webservice/servers/endpoint.php?JSON";

    public NetcupDnsProviderAdapter(IHttpClientFactory factory) : base(factory)
    {
    }

    public bool Supports(string providerType) => string.Equals(providerType, "netcup", StringComparison.OrdinalIgnoreCase);

    public async Task<ProviderOperationResult> TestAsync(DnsProviderContext context, string domain, CancellationToken cancellationToken = default)
    {
        var session = await LoginAsync(context, cancellationToken);
        try
        {
            using var response = await RequestAsync(context, "infoDnsZone", new
            {
                domainname = domain,
                customernumber = OperationsJson.Required(context.Settings, "customer_number"),
                apikey = OperationsJson.Required(context.Secrets, "api_key"),
                apisessionid = session,
            }, cancellationToken);
            return ProviderOperationResult.Success($"Netcup DNS zone '{domain}' is reachable.");
        }
        finally
        {
            await LogoutAsync(context, session, cancellationToken);
        }
    }

    public async Task<ProviderOperationResult> UpsertAsync(DnsProviderContext context, DnsRecordMutation mutation, CancellationToken cancellationToken = default)
    {
        var session = await LoginAsync(context, cancellationToken);
        try
        {
            using var existing = await RequestAsync(context, "infoDnsRecords", BaseParameters(context, mutation.Domain, session), cancellationToken);
            var records = new List<Dictionary<string, object?>>();
            if (existing.RootElement.TryGetProperty("responsedata", out var responseData) &&
                responseData.TryGetProperty("dnsrecords", out var dnsRecords) &&
                dnsRecords.ValueKind == JsonValueKind.Array)
            {
                foreach (var record in dnsRecords.EnumerateArray())
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, object?>>(record.GetRawText()) ?? [];
                    var host = record.TryGetProperty("hostname", out var hostProperty) ? hostProperty.GetString() : string.Empty;
                    var type = record.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : string.Empty;
                    if (string.Equals(host, RelativeName(mutation), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(type, mutation.RecordType, StringComparison.OrdinalIgnoreCase))
                    {
                        map["destination"] = mutation.Value;
                        map["deleterecord"] = false;
                    }

                    records.Add(map);
                }
            }

            if (!records.Any(record =>
                    string.Equals(Convert.ToString(record.GetValueOrDefault("hostname"), System.Globalization.CultureInfo.InvariantCulture), RelativeName(mutation), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Convert.ToString(record.GetValueOrDefault("type"), System.Globalization.CultureInfo.InvariantCulture), mutation.RecordType, StringComparison.OrdinalIgnoreCase)))
            {
                records.Add(new Dictionary<string, object?>
                {
                    ["hostname"] = RelativeName(mutation),
                    ["type"] = mutation.RecordType,
                    ["priority"] = mutation.Priority?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
                    ["destination"] = mutation.Value,
                    ["deleterecord"] = false,
                    ["state"] = "yes",
                });
            }

            var parameters = BaseParameters(context, mutation.Domain, session);
            parameters["dnsrecordset"] = new Dictionary<string, object?> { ["dnsrecords"] = records };
            using var updated = await RequestAsync(context, "updateDnsRecords", parameters, cancellationToken);
            return ProviderOperationResult.Success($"Netcup updated {mutation.RecordType} {mutation.Fqdn}.");
        }
        finally
        {
            await LogoutAsync(context, session, cancellationToken);
        }
    }

    private async Task<string> LoginAsync(DnsProviderContext context, CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(context, "login", new
        {
            customernumber = OperationsJson.Required(context.Settings, "customer_number"),
            apikey = OperationsJson.Required(context.Secrets, "api_key"),
            apipassword = OperationsJson.Required(context.Secrets, "api_password"),
        }, cancellationToken);
        var data = response.RootElement.GetProperty("responsedata");
        return data.TryGetProperty("apisessionid", out var session) && !string.IsNullOrWhiteSpace(session.GetString())
            ? session.GetString()!
            : throw new InvalidOperationException("Netcup login did not return an API session ID.");
    }

    private async Task LogoutAsync(DnsProviderContext context, string session, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await RequestAsync(context, "logout", new
            {
                customernumber = OperationsJson.Required(context.Settings, "customer_number"),
                apikey = OperationsJson.Required(context.Secrets, "api_key"),
                apisessionid = session,
            }, cancellationToken);
        }
        catch (Exception)
        {
        }
    }

    private static Dictionary<string, object?> BaseParameters(DnsProviderContext context, string domain, string session)
    {
        return new Dictionary<string, object?>
        {
            ["domainname"] = domain,
            ["customernumber"] = OperationsJson.Required(context.Settings, "customer_number"),
            ["apikey"] = OperationsJson.Required(context.Secrets, "api_key"),
            ["apisessionid"] = session,
        };
    }

    private async Task<JsonDocument> RequestAsync(DnsProviderContext context, string action, object parameters, CancellationToken cancellationToken)
    {
        var endpoint = OperationsJson.Optional(context.Settings, "endpoint", DefaultEndpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent(new { action, param = parameters }),
        };
        var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        if (!response.RootElement.TryGetProperty("status", out var status) || status.GetString() != "success")
        {
            var message = response.RootElement.TryGetProperty("longmessage", out var longMessage)
                ? longMessage.GetString()
                : response.RootElement.TryGetProperty("shortmessage", out var shortMessage)
                    ? shortMessage.GetString()
                    : "Netcup API returned an unsuccessful response.";
            response.Dispose();
            throw new InvalidOperationException(message);
        }

        return response;
    }
}

public sealed class CommonRestDnsProviderAdapter : HttpDnsProviderAdapter, IDnsProviderAdapter
{
    private static readonly HashSet<string> Supported = new(
        ["cloudflare", "digitalocean", "hetzner", "ionos", "gandi", "desec", "duckdns"],
        StringComparer.OrdinalIgnoreCase);

    public CommonRestDnsProviderAdapter(IHttpClientFactory factory) : base(factory)
    {
    }

    public bool Supports(string providerType) => Supported.Contains(providerType);

    public Task<ProviderOperationResult> TestAsync(DnsProviderContext context, string domain, CancellationToken cancellationToken = default)
    {
        return context.Provider.ProviderType.ToLowerInvariant() switch
        {
            "cloudflare" => TestCloudflareAsync(context, domain, cancellationToken),
            "digitalocean" => TestBearerEndpointAsync(context, $"https://api.digitalocean.com/v2/domains/{Uri.EscapeDataString(domain)}", "token", "DigitalOcean", cancellationToken),
            "hetzner" => TestTokenHeaderEndpointAsync(context, $"https://dns.hetzner.com/api/v1/zones?name={Uri.EscapeDataString(domain)}", "Auth-API-Token", "api_token", "Hetzner", cancellationToken),
            "ionos" => TestTokenHeaderEndpointAsync(context, $"https://api.hosting.ionos.com/dns/v1/zones/{Uri.EscapeDataString(domain)}", "X-API-Key", "api_key", "IONOS", cancellationToken),
            "gandi" => TestBearerEndpointAsync(context, $"https://api.gandi.net/v5/livedns/domains/{Uri.EscapeDataString(domain)}", "personal_access_token", "Gandi", cancellationToken),
            "desec" => TestTokenHeaderEndpointAsync(context, $"https://desec.io/api/v1/domains/{Uri.EscapeDataString(domain)}/", "Authorization", "token", "deSEC", cancellationToken, "Token "),
            "duckdns" => TestDuckDnsAsync(context, domain, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported provider runtime adapter."),
        };
    }

    public Task<ProviderOperationResult> UpsertAsync(DnsProviderContext context, DnsRecordMutation mutation, CancellationToken cancellationToken = default)
    {
        return context.Provider.ProviderType.ToLowerInvariant() switch
        {
            "cloudflare" => UpsertCloudflareAsync(context, mutation, cancellationToken),
            "digitalocean" => UpsertDigitalOceanAsync(context, mutation, cancellationToken),
            "hetzner" => UpsertHetznerAsync(context, mutation, cancellationToken),
            "ionos" => UpsertIonosAsync(context, mutation, cancellationToken),
            "gandi" => UpsertGandiAsync(context, mutation, cancellationToken),
            "desec" => UpsertDesecAsync(context, mutation, cancellationToken),
            "duckdns" => UpsertDuckDnsAsync(context, mutation, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported provider runtime adapter."),
        };
    }

    private async Task<ProviderOperationResult> TestCloudflareAsync(DnsProviderContext context, string domain, CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "api_token");
        using var request = Bearer(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones?name={Uri.EscapeDataString(domain)}&status=active", token);
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        if (!response.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            throw new InvalidOperationException("Cloudflare rejected the API token or zone lookup.");
        }

        return ProviderOperationResult.Success($"Cloudflare zone '{domain}' is reachable.");
    }

    private async Task<ProviderOperationResult> UpsertCloudflareAsync(DnsProviderContext context, DnsRecordMutation mutation, CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "api_token");
        var client = CreateClient();
        using var zoneRequest = Bearer(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones?name={Uri.EscapeDataString(mutation.Domain)}&status=active", token);
        using var zoneResponse = await SendJsonAsync(client, zoneRequest, cancellationToken);
        var zoneId = zoneResponse.RootElement.GetProperty("result").EnumerateArray().Select(item => item.GetProperty("id").GetString()).FirstOrDefault() ??
            throw new InvalidOperationException("Cloudflare zone was not found.");
        using var lookupRequest = Bearer(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?type={mutation.RecordType}&name={Uri.EscapeDataString(mutation.Fqdn)}", token);
        using var lookup = await SendJsonAsync(client, lookupRequest, cancellationToken);
        var recordId = lookup.RootElement.GetProperty("result").EnumerateArray().Select(item => item.GetProperty("id").GetString()).FirstOrDefault();
        var method = recordId is null ? HttpMethod.Post : HttpMethod.Put;
        var uri = recordId is null
            ? $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records"
            : $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records/{recordId}";
        using var write = Bearer(method, uri, token);
        write.Content = JsonContent(new { type = mutation.RecordType, name = mutation.Fqdn, content = mutation.Value, ttl = mutation.Ttl, proxied = false });
        using var result = await SendJsonAsync(client, write, cancellationToken);
        return ProviderOperationResult.Success($"Cloudflare updated {mutation.RecordType} {mutation.Fqdn}.");
    }

    private async Task<ProviderOperationResult> UpsertDigitalOceanAsync(DnsProviderContext context, DnsRecordMutation mutation, CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "token");
        var client = CreateClient();
        using var lookupRequest = Bearer(HttpMethod.Get, $"https://api.digitalocean.com/v2/domains/{Uri.EscapeDataString(mutation.Domain)}/records?type={mutation.RecordType}&name={Uri.EscapeDataString(RelativeName(mutation))}", token);
        using var lookup = await SendJsonAsync(client, lookupRequest, cancellationToken);
        var recordId = lookup.RootElement.TryGetProperty("domain_records", out var records)
            ? records.EnumerateArray().Select(item => item.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault()
            : null;
        var uri = recordId is null
            ? $"https://api.digitalocean.com/v2/domains/{Uri.EscapeDataString(mutation.Domain)}/records"
            : $"https://api.digitalocean.com/v2/domains/{Uri.EscapeDataString(mutation.Domain)}/records/{recordId}";
        using var request = Bearer(recordId is null ? HttpMethod.Post : HttpMethod.Put, uri, token);
        request.Content = JsonContent(new { type = mutation.RecordType, name = RelativeName(mutation), data = mutation.Value, ttl = mutation.Ttl, priority = mutation.Priority });
        using var response = await SendJsonAsync(client, request, cancellationToken);
        return ProviderOperationResult.Success($"DigitalOcean updated {mutation.RecordType} {mutation.Fqdn}.");
    }

    private async Task<ProviderOperationResult> UpsertHetznerAsync(DnsProviderContext context, DnsRecordMutation mutation, CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "api_token");
        var client = CreateClient();
        using var zoneRequest = new HttpRequestMessage(HttpMethod.Get, $"https://dns.hetzner.com/api/v1/zones?name={Uri.EscapeDataString(mutation.Domain)}");
        zoneRequest.Headers.Add("Auth-API-Token", token);
        using var zoneResponse = await SendJsonAsync(client, zoneRequest, cancellationToken);
        var zoneId = zoneResponse.RootElement.GetProperty("zones").EnumerateArray().Select(item => item.GetProperty("id").GetString()).FirstOrDefault() ??
            throw new InvalidOperationException("Hetzner DNS zone was not found.");
        using var recordsRequest = new HttpRequestMessage(HttpMethod.Get, $"https://dns.hetzner.com/api/v1/records?zone_id={zoneId}");
        recordsRequest.Headers.Add("Auth-API-Token", token);
        using var records = await SendJsonAsync(client, recordsRequest, cancellationToken);
        var recordId = records.RootElement.GetProperty("records").EnumerateArray()
            .Where(item => string.Equals(item.GetProperty("type").GetString(), mutation.RecordType, StringComparison.OrdinalIgnoreCase) && string.Equals(item.GetProperty("name").GetString(), RelativeName(mutation), StringComparison.OrdinalIgnoreCase))
            .Select(item => item.GetProperty("id").GetString()).FirstOrDefault();
        using var request = new HttpRequestMessage(recordId is null ? HttpMethod.Post : HttpMethod.Put, recordId is null ? "https://dns.hetzner.com/api/v1/records" : $"https://dns.hetzner.com/api/v1/records/{recordId}");
        request.Headers.Add("Auth-API-Token", token);
        request.Content = JsonContent(new { zone_id = zoneId, type = mutation.RecordType, name = RelativeName(mutation), value = mutation.Value, ttl = mutation.Ttl });
        using var response = await SendJsonAsync(client, request, cancellationToken);
        return ProviderOperationResult.Success($"Hetzner updated {mutation.RecordType} {mutation.Fqdn}.");
    }

    private async Task<ProviderOperationResult> UpsertIonosAsync(DnsProviderContext context, DnsRecordMutation mutation, CancellationToken cancellationToken)
    {
        var key = OperationsJson.Required(context.Secrets, "api_key");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"https://api.hosting.ionos.com/dns/v1/zones/{Uri.EscapeDataString(mutation.Domain)}");
        request.Headers.Add("X-API-Key", key);
        request.Content = JsonContent(new[] { new { name = mutation.Fqdn, type = mutation.RecordType, content = mutation.Value, ttl = mutation.Ttl, prio = mutation.Priority, disabled = false } });
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        return ProviderOperationResult.Success($"IONOS updated {mutation.RecordType} {mutation.Fqdn}.");
    }

    private async Task<ProviderOperationResult> UpsertGandiAsync(DnsProviderContext context, DnsRecordMutation mutation, CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "personal_access_token");
        var name = RelativeName(mutation) == "@" ? "@" : RelativeName(mutation);
        using var request = Bearer(HttpMethod.Put, $"https://api.gandi.net/v5/livedns/domains/{Uri.EscapeDataString(mutation.Domain)}/records/{Uri.EscapeDataString(name)}/{mutation.RecordType}", token);
        request.Content = JsonContent(new { rrset_ttl = mutation.Ttl, rrset_values = new[] { mutation.Value } });
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        return ProviderOperationResult.Success($"Gandi updated {mutation.RecordType} {mutation.Fqdn}.");
    }

    private async Task<ProviderOperationResult> UpsertDesecAsync(DnsProviderContext context, DnsRecordMutation mutation, CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "token");
        var name = RelativeName(mutation) == "@" ? "@" : RelativeName(mutation);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"https://desec.io/api/v1/domains/{Uri.EscapeDataString(mutation.Domain)}/rrsets/{Uri.EscapeDataString(name)}/{mutation.RecordType}/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        request.Content = JsonContent(new { subname = name, type = mutation.RecordType, ttl = mutation.Ttl, records = new[] { mutation.Value } });
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        return ProviderOperationResult.Success($"deSEC updated {mutation.RecordType} {mutation.Fqdn}.");
    }

    private async Task<ProviderOperationResult> TestDuckDnsAsync(DnsProviderContext context, string domain, CancellationToken cancellationToken)
    {
        var configured = OperationsJson.Required(context.Settings, "domain");
        if (!domain.EndsWith("duckdns.org", StringComparison.OrdinalIgnoreCase) && !string.Equals(domain, configured, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected domain does not match the configured DuckDNS subdomain.");
        }

        var token = OperationsJson.Required(context.Secrets, "token");
        using var response = await CreateClient().GetAsync($"https://www.duckdns.org/update?domains={Uri.EscapeDataString(configured)}&token={Uri.EscapeDataString(token)}&verbose=true", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || !body.Contains("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DuckDNS rejected the provider test.");
        }

        return ProviderOperationResult.Success($"DuckDNS subdomain '{configured}' is reachable.");
    }

    private async Task<ProviderOperationResult> UpsertDuckDnsAsync(DnsProviderContext context, DnsRecordMutation mutation, CancellationToken cancellationToken)
    {
        if (mutation.RecordType is not ("A" or "AAAA"))
        {
            throw new InvalidOperationException("DuckDNS direct updates support A and AAAA records only.");
        }

        var domain = OperationsJson.Required(context.Settings, "domain");
        var token = OperationsJson.Required(context.Secrets, "token");
        var parameter = mutation.RecordType == "A" ? "ip" : "ipv6";
        using var response = await CreateClient().GetAsync($"https://www.duckdns.org/update?domains={Uri.EscapeDataString(domain)}&token={Uri.EscapeDataString(token)}&{parameter}={Uri.EscapeDataString(mutation.Value)}&verbose=true", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || !body.Contains("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DuckDNS rejected the record update.");
        }

        return ProviderOperationResult.Success($"DuckDNS updated {mutation.RecordType} {mutation.Fqdn}.");
    }

    private async Task<ProviderOperationResult> TestBearerEndpointAsync(DnsProviderContext context, string endpoint, string secretKey, string displayName, CancellationToken cancellationToken)
    {
        using var request = Bearer(HttpMethod.Get, endpoint, OperationsJson.Required(context.Secrets, secretKey));
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        return ProviderOperationResult.Success($"{displayName} provider access is valid.");
    }

    private async Task<ProviderOperationResult> TestTokenHeaderEndpointAsync(DnsProviderContext context, string endpoint, string headerName, string secretKey, string displayName, CancellationToken cancellationToken, string prefix = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation(headerName, prefix + OperationsJson.Required(context.Secrets, secretKey));
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        return ProviderOperationResult.Success($"{displayName} provider access is valid.");
    }
}
