namespace CaddyUi.Application.Dns;

[Flags]
public enum DnsProviderCapability
{
    None = 0,
    ZoneDiscovery = 1,
    RecordManagement = 2,
    DnsChallenge = 4,
    DynamicDns = 8,
}

public sealed record DnsProviderField(
    string Key,
    string Label,
    bool Required,
    string? DefaultValue = null,
    string? HelpText = null);

public sealed record DnsProviderDefinition(
    string Type,
    string DisplayName,
    DnsProviderCapability Capabilities,
    IReadOnlyList<DnsProviderField> Settings,
    IReadOnlyList<DnsProviderField> Secrets,
    string DocumentationHint);

public static class DnsProviderCatalog
{
    public static IReadOnlyList<DnsProviderDefinition> All { get; } =
    [
        Provider(
            "netcup",
            "Netcup",
            DnsProviderCapability.ZoneDiscovery | DnsProviderCapability.RecordManagement | DnsProviderCapability.DnsChallenge | DnsProviderCapability.DynamicDns,
            [Field("customer_number", "Customer number", true)],
            [Secret("api_key", "API key"), Secret("api_password", "API password")],
            "Netcup DNS API"),
        Provider(
            "cloudflare",
            "Cloudflare",
            Full,
            [],
            [Secret("api_token", "API token")],
            "Token with Zone:Read and DNS:Edit"),
        Provider(
            "route53",
            "Amazon Route 53",
            Full,
            [Field("region", "Region", false, "eu-central-1")],
            [Secret("access_key_id", "Access Key ID"), Secret("secret_access_key", "Secret Access Key")],
            "AWS IAM credentials"),
        Provider(
            "digitalocean",
            "DigitalOcean",
            Full,
            [],
            [Secret("token", "Personal Access Token")],
            "DigitalOcean DNS API"),
        Provider(
            "hetzner",
            "Hetzner DNS",
            Full,
            [],
            [Secret("api_token", "API token")],
            "Hetzner DNS Console"),
        Provider(
            "ionos",
            "IONOS",
            Full,
            [],
            [Secret("api_key", "API key")],
            "IONOS DNS API"),
        Provider(
            "ovh",
            "OVHcloud",
            Full,
            [Field("endpoint", "API endpoint", true, "ovh-eu")],
            [
                Secret("application_key", "Application Key"),
                Secret("application_secret", "Application Secret"),
                Secret("consumer_key", "Consumer Key"),
            ],
            "OVH API v1"),
        Provider(
            "porkbun",
            "Porkbun",
            Full,
            [],
            [Secret("api_key", "API key"), Secret("secret_key", "Secret Key")],
            "Porkbun DNS API"),
        Provider(
            "namecheap",
            "Namecheap",
            Full,
            [
                Field("api_user", "API user", true),
                Field("username", "Username", true),
                Field("client_ip", "Allowed client IP", true),
            ],
            [Secret("api_key", "API key")],
            "Namecheap XML API"),
        Provider(
            "gandi",
            "Gandi LiveDNS",
            Full,
            [],
            [Secret("personal_access_token", "Personal Access Token")],
            "Gandi LiveDNS API"),
        Provider(
            "desec",
            "deSEC",
            Full,
            [],
            [Secret("token", "API token")],
            "deSEC DNS API"),
        Provider(
            "google-cloud-dns",
            "Google Cloud DNS",
            Full,
            [Field("project_id", "Project ID", true)],
            [Secret("service_account_json", "Service account JSON")],
            "Google Cloud DNS API"),
        Provider(
            "azure-dns",
            "Microsoft Azure DNS",
            Full,
            [
                Field("tenant_id", "Tenant ID", true),
                Field("client_id", "Client ID", true),
                Field("subscription_id", "Subscription ID", true),
                Field("resource_group", "Resource group", true),
            ],
            [Secret("client_secret", "Client Secret")],
            "Azure Resource Manager DNS API"),
        Provider(
            "vultr",
            "Vultr",
            Full,
            [],
            [Secret("api_key", "API key")],
            "Vultr DNS API"),
        Provider(
            "linode",
            "Akamai Connected Cloud (Linode)",
            Full,
            [],
            [Secret("token", "Personal Access Token")],
            "Linode Domains API"),
        Provider(
            "godaddy",
            "GoDaddy",
            Full,
            [],
            [Secret("api_key", "API key"), Secret("api_secret", "API secret")],
            "GoDaddy Domains API"),
        Provider(
            "duckdns",
            "DuckDNS",
            DnsProviderCapability.RecordManagement | DnsProviderCapability.DnsChallenge | DnsProviderCapability.DynamicDns,
            [Field("domain", "DuckDNS subdomain", true)],
            [Secret("token", "Token")],
            "DuckDNS Update API"),
        Provider(
            "rfc2136",
            "RFC 2136 / custom DNS server",
            DnsProviderCapability.RecordManagement | DnsProviderCapability.DnsChallenge | DnsProviderCapability.DynamicDns,
            [
                Field("server", "DNS server", true),
                Field("key_name", "TSIG key name", true),
                Field("algorithm", "TSIG algorithm", true, "hmac-sha256"),
            ],
            [Secret("shared_secret", "TSIG shared secret")],
            "Standard dynamic DNS updates"),
    ];

    private const DnsProviderCapability Full =
        DnsProviderCapability.ZoneDiscovery |
        DnsProviderCapability.RecordManagement |
        DnsProviderCapability.DnsChallenge |
        DnsProviderCapability.DynamicDns;

    public static DnsProviderDefinition? Find(string? type)
    {
        return All.FirstOrDefault(item =>
            string.Equals(item.Type, type?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static DnsProviderDefinition Provider(
        string type,
        string name,
        DnsProviderCapability capabilities,
        IReadOnlyList<DnsProviderField> settings,
        IReadOnlyList<DnsProviderField> secrets,
        string hint)
    {
        return new DnsProviderDefinition(type, name, capabilities, settings, secrets, hint);
    }

    private static DnsProviderField Field(
        string key,
        string label,
        bool required,
        string? defaultValue = null)
    {
        return new DnsProviderField(
            key,
            label,
            required,
            defaultValue,
            SettingHelp(key, defaultValue));
    }

    private static DnsProviderField Secret(string key, string label)
    {
        return new DnsProviderField(
            key,
            label,
            Required: true,
            HelpText: $"Enter the {SecretPurpose(key)} directly; it is stored encrypted. A secret://env/ or secret://file/ reference can be used instead.");
    }

    private static string SettingHelp(string key, string? defaultValue)
    {
        var explanation = key switch
        {
            "customer_number" => "Netcup customer number from the Customer Control Panel.",
            "region" => "AWS region used for DNS management; the default usually applies.",
            "endpoint" => "OVH API region matching the customer account.",
            "api_user" => "Namecheap user for which API access is enabled.",
            "username" => "Namecheap account that owns the domain.",
            "client_ip" => "Public server IP allowed for Namecheap API access.",
            "project_id" => "Google Cloud project containing the DNS zone.",
            "tenant_id" => "Azure Entra tenant ID of the app registration.",
            "client_id" => "Client ID of the Azure app registration.",
            "subscription_id" => "Azure subscription containing the DNS zone.",
            "resource_group" => "Azure resource group containing the DNS zone.",
            "domain" => "DuckDNS name without .duckdns.org.",
            "server" => "DNS server with optional port, for example 10.0.0.53:53.",
            "key_name" => "Name of the TSIG key on the DNS server.",
            "algorithm" => "TSIG signature algorithm configured on the DNS server.",
            _ => "Non-secret provider setting.",
        };
        return defaultValue is null ? explanation : $"{explanation} Default: {defaultValue}.";
    }

    private static string SecretPurpose(string key)
    {
        return key.Replace('_', ' ');
    }
}
