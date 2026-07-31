using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CaddyUi.Infrastructure.Operations;

public interface IDnsProviderRecordReader
{
    bool Supports(string providerType);

    Task<IReadOnlyList<ProviderDnsRecord>> ListAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken = default);
}

public sealed class DnsProviderRecordQueryService
{
    private readonly OperationsStore _store;
    private readonly ISecretReferenceResolver _secrets;
    private readonly IReadOnlyList<IDnsProviderRecordReader> _readers;

    public DnsProviderRecordQueryService(
        OperationsStore store,
        ISecretReferenceResolver secrets,
        IEnumerable<IDnsProviderRecordReader> readers)
    {
        _store = store;
        _secrets = secrets;
        _readers = readers.ToArray();
    }

    public bool CanList(string providerType)
    {
        return _readers.Any(reader => reader.Supports(providerType));
    }

    public async Task<IReadOnlyList<ProviderDnsRecord>> ListAsync(
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

        var reader = _readers.FirstOrDefault(candidate => candidate.Supports(provider.ProviderType)) ??
            throw new InvalidOperationException(
                $"Provider '{provider.ProviderType}' does not expose a supported record-list API.");
        var context = await CreateContextAsync(provider, cancellationToken);
        var records = await reader.ListAsync(context, NormalizeDomain(domain), cancellationToken);
        return records
            .OrderBy(record => record.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.RecordType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Value, StringComparer.Ordinal)
            .ToArray();
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

        return new DnsProviderContext(
            provider,
            OperationsJson.ReadStringObject(provider.ConfigJson),
            resolved);
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
}

public sealed class NetcupDnsProviderRecordReader : HttpDnsProviderAdapter, IDnsProviderRecordReader
{
    private const string DefaultEndpoint = "https://ccp.netcup.net/run/webservice/servers/endpoint.php?JSON";

    public NetcupDnsProviderRecordReader(IHttpClientFactory factory)
        : base(factory)
    {
    }

    public bool Supports(string providerType)
    {
        return string.Equals(providerType, "netcup", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<ProviderDnsRecord>> ListAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken = default)
    {
        var session = await LoginAsync(context, cancellationToken);
        try
        {
            using var zone = await RequestAsync(
                context,
                "infoDnsZone",
                BaseParameters(context, domain, session),
                cancellationToken);
            var zoneTtl = zone.RootElement.TryGetProperty("responsedata", out var zoneData)
                ? ReadInt(zoneData, "ttl")
                : null;

            using var response = await RequestAsync(
                context,
                "infoDnsRecords",
                BaseParameters(context, domain, session),
                cancellationToken);
            if (!response.RootElement.TryGetProperty("responsedata", out var responseData) ||
                !responseData.TryGetProperty("dnsrecords", out var records) ||
                records.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ProviderDnsRecord>();
            }

            return records.EnumerateArray()
                .Select(record => new ProviderDnsRecord(
                    ReadString(record, "id"),
                    ReadString(record, "hostname", "@"),
                    ReadString(record, "type"),
                    ReadString(record, "destination"),
                    ReadInt(record, "ttl") ?? zoneTtl,
                    ReadInt(record, "priority"),
                    !ReadBool(record, "deleterecord") &&
                    !string.Equals(ReadString(record, "state", "yes"), "no", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        finally
        {
            await LogoutAsync(context, session, cancellationToken);
        }
    }

    private async Task<string> LoginAsync(
        DnsProviderContext context,
        CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(
            context,
            "login",
            new
            {
                customernumber = OperationsJson.Required(context.Settings, "customer_number"),
                apikey = OperationsJson.Required(context.Secrets, "api_key"),
                apipassword = OperationsJson.Required(context.Secrets, "api_password"),
            },
            cancellationToken);
        var data = response.RootElement.GetProperty("responsedata");
        return data.TryGetProperty("apisessionid", out var session) &&
               !string.IsNullOrWhiteSpace(session.GetString())
            ? session.GetString()!
            : throw new InvalidOperationException("Netcup login did not return an API session ID.");
    }

    private async Task LogoutAsync(
        DnsProviderContext context,
        string session,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await RequestAsync(
                context,
                "logout",
                BaseParameters(context, string.Empty, session, includeDomain: false),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    private static Dictionary<string, object?> BaseParameters(
        DnsProviderContext context,
        string domain,
        string session,
        bool includeDomain = true)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["customernumber"] = OperationsJson.Required(context.Settings, "customer_number"),
            ["apikey"] = OperationsJson.Required(context.Secrets, "api_key"),
            ["apisessionid"] = session,
        };
        if (includeDomain)
        {
            parameters["domainname"] = domain;
        }

        return parameters;
    }

    private async Task<JsonDocument> RequestAsync(
        DnsProviderContext context,
        string action,
        object parameters,
        CancellationToken cancellationToken)
    {
        var endpoint = OperationsJson.Optional(context.Settings, "endpoint", DefaultEndpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent(new { action, param = parameters }),
        };
        var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        if (!response.RootElement.TryGetProperty("status", out var status) ||
            !string.Equals(status.GetString(), "success", StringComparison.Ordinal))
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

    private static string ReadString(JsonElement element, string property, string fallback = "")
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : value.ToString();
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool ReadBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True ||
               string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value.ToString(), "yes", StringComparison.OrdinalIgnoreCase) ||
               value.ToString() == "1";
    }
}

public sealed class CommonRestDnsProviderRecordReader : HttpDnsProviderAdapter, IDnsProviderRecordReader
{
    private static readonly HashSet<string> Supported = new(
        ["cloudflare", "digitalocean", "hetzner", "ionos", "gandi", "desec"],
        StringComparer.OrdinalIgnoreCase);

    public CommonRestDnsProviderRecordReader(IHttpClientFactory factory)
        : base(factory)
    {
    }

    public bool Supports(string providerType)
    {
        return Supported.Contains(providerType);
    }

    public Task<IReadOnlyList<ProviderDnsRecord>> ListAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken = default)
    {
        return context.Provider.ProviderType.ToLowerInvariant() switch
        {
            "cloudflare" => ListCloudflareAsync(context, domain, cancellationToken),
            "digitalocean" => ListDigitalOceanAsync(context, domain, cancellationToken),
            "hetzner" => ListHetznerAsync(context, domain, cancellationToken),
            "ionos" => ListIonosAsync(context, domain, cancellationToken),
            "gandi" => ListGandiAsync(context, domain, cancellationToken),
            "desec" => ListDesecAsync(context, domain, cancellationToken),
            _ => throw new InvalidOperationException("This provider does not support record listing."),
        };
    }

    private async Task<IReadOnlyList<ProviderDnsRecord>> ListCloudflareAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "api_token");
        var client = CreateClient();
        using var zoneRequest = Bearer(
            HttpMethod.Get,
            $"https://api.cloudflare.com/client/v4/zones?name={Uri.EscapeDataString(domain)}&status=active",
            token);
        using var zoneResponse = await SendJsonAsync(client, zoneRequest, cancellationToken);
        var zoneId = zoneResponse.RootElement.GetProperty("result")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .FirstOrDefault() ?? throw new InvalidOperationException("Cloudflare zone was not found.");
        using var recordsRequest = Bearer(
            HttpMethod.Get,
            $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?per_page=5000",
            token);
        using var response = await SendJsonAsync(client, recordsRequest, cancellationToken);
        return response.RootElement.GetProperty("result").EnumerateArray()
            .Select(record => new ProviderDnsRecord(
                String(record, "id"),
                RelativeName(String(record, "name"), domain),
                String(record, "type"),
                String(record, "content"),
                Integer(record, "ttl"),
                Integer(record, "priority"),
                true))
            .ToArray();
    }

    private async Task<IReadOnlyList<ProviderDnsRecord>> ListDigitalOceanAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "token");
        using var request = Bearer(
            HttpMethod.Get,
            $"https://api.digitalocean.com/v2/domains/{Uri.EscapeDataString(domain)}/records?per_page=200",
            token);
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        return response.RootElement.GetProperty("domain_records").EnumerateArray()
            .Select(record => new ProviderDnsRecord(
                String(record, "id"),
                String(record, "name", "@"),
                String(record, "type"),
                String(record, "data"),
                Integer(record, "ttl"),
                Integer(record, "priority"),
                true))
            .ToArray();
    }

    private async Task<IReadOnlyList<ProviderDnsRecord>> ListHetznerAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "api_token");
        var client = CreateClient();
        using var zoneRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://dns.hetzner.com/api/v1/zones?name={Uri.EscapeDataString(domain)}");
        zoneRequest.Headers.Add("Auth-API-Token", token);
        using var zoneResponse = await SendJsonAsync(client, zoneRequest, cancellationToken);
        var zoneId = zoneResponse.RootElement.GetProperty("zones")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .FirstOrDefault() ?? throw new InvalidOperationException("Hetzner DNS zone was not found.");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://dns.hetzner.com/api/v1/records?zone_id={zoneId}");
        request.Headers.Add("Auth-API-Token", token);
        using var response = await SendJsonAsync(client, request, cancellationToken);
        return response.RootElement.GetProperty("records").EnumerateArray()
            .Select(record => new ProviderDnsRecord(
                String(record, "id"),
                String(record, "name", "@"),
                String(record, "type"),
                String(record, "value"),
                Integer(record, "ttl"),
                null,
                true))
            .ToArray();
    }

    private async Task<IReadOnlyList<ProviderDnsRecord>> ListIonosAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken)
    {
        var key = OperationsJson.Required(context.Secrets, "api_key");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.hosting.ionos.com/dns/v1/zones/{Uri.EscapeDataString(domain)}");
        request.Headers.Add("X-API-Key", key);
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        var records = response.RootElement.ValueKind == JsonValueKind.Array
            ? response.RootElement
            : response.RootElement.TryGetProperty("records", out var nested)
                ? nested
                : default;
        if (records.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProviderDnsRecord>();
        }

        return records.EnumerateArray()
            .Select(record => new ProviderDnsRecord(
                String(record, "id"),
                RelativeName(String(record, "name"), domain),
                String(record, "type"),
                String(record, "content"),
                Integer(record, "ttl"),
                Integer(record, "prio"),
                !Boolean(record, "disabled")))
            .ToArray();
    }

    private async Task<IReadOnlyList<ProviderDnsRecord>> ListGandiAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "personal_access_token");
        using var request = Bearer(
            HttpMethod.Get,
            $"https://api.gandi.net/v5/livedns/domains/{Uri.EscapeDataString(domain)}/records",
            token);
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        var result = new List<ProviderDnsRecord>();
        foreach (var set in response.RootElement.EnumerateArray())
        {
            var name = String(set, "rrset_name", "@");
            var type = String(set, "rrset_type");
            var ttl = Integer(set, "rrset_ttl");
            if (!set.TryGetProperty("rrset_values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var index = 0;
            foreach (var value in values.EnumerateArray())
            {
                result.Add(new ProviderDnsRecord(
                    $"{name}:{type}:{index++}",
                    name,
                    type,
                    value.GetString() ?? value.ToString(),
                    ttl,
                    null,
                    true));
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<ProviderDnsRecord>> ListDesecAsync(
        DnsProviderContext context,
        string domain,
        CancellationToken cancellationToken)
    {
        var token = OperationsJson.Required(context.Secrets, "token");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://desec.io/api/v1/domains/{Uri.EscapeDataString(domain)}/rrsets/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var response = await SendJsonAsync(CreateClient(), request, cancellationToken);
        var result = new List<ProviderDnsRecord>();
        foreach (var set in response.RootElement.EnumerateArray())
        {
            var name = String(set, "subname", "@");
            var type = String(set, "type");
            var ttl = Integer(set, "ttl");
            if (!set.TryGetProperty("records", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var index = 0;
            foreach (var value in values.EnumerateArray())
            {
                result.Add(new ProviderDnsRecord(
                    $"{name}:{type}:{index++}",
                    name,
                    type,
                    value.GetString() ?? value.ToString(),
                    ttl,
                    null,
                    true));
            }
        }

        return result;
    }

    private static string RelativeName(string name, string domain)
    {
        var normalized = name.TrimEnd('.');
        if (string.Equals(normalized, domain, StringComparison.OrdinalIgnoreCase))
        {
            return "@";
        }

        var suffix = $".{domain}";
        return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^suffix.Length]
            : normalized;
    }

    private static string String(JsonElement element, string property, string fallback = "")
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : value.ToString();
    }

    private static int? Integer(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool Boolean(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) &&
               (value.ValueKind == JsonValueKind.True ||
                string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase));
    }
}
