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
            [Field("customer_number", "Kundennummer", true)],
            [Secret("api_key", "API-Key"), Secret("api_password", "API-Passwort")],
            "Netcup DNS API"),
        Provider(
            "cloudflare",
            "Cloudflare",
            Full,
            [],
            [Secret("api_token", "API-Token")],
            "Token mit Zone:Read und DNS:Edit"),
        Provider(
            "route53",
            "Amazon Route 53",
            Full,
            [Field("region", "Region", false, "eu-central-1")],
            [Secret("access_key_id", "Access Key ID"), Secret("secret_access_key", "Secret Access Key")],
            "AWS IAM-Zugangsdaten"),
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
            [Secret("api_token", "API-Token")],
            "Hetzner DNS Console"),
        Provider(
            "ionos",
            "IONOS",
            Full,
            [],
            [Secret("api_key", "API-Key")],
            "IONOS DNS API"),
        Provider(
            "ovh",
            "OVHcloud",
            Full,
            [Field("endpoint", "API-Endpunkt", true, "ovh-eu")],
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
            [Secret("api_key", "API-Key"), Secret("secret_key", "Secret Key")],
            "Porkbun DNS API"),
        Provider(
            "namecheap",
            "Namecheap",
            Full,
            [
                Field("api_user", "API-Benutzer", true),
                Field("username", "Benutzername", true),
                Field("client_ip", "Freigeschaltete Client-IP", true),
            ],
            [Secret("api_key", "API-Key")],
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
            [Secret("token", "API-Token")],
            "deSEC DNS API"),
        Provider(
            "google-cloud-dns",
            "Google Cloud DNS",
            Full,
            [Field("project_id", "Projekt-ID", true)],
            [Secret("service_account_json", "Service-Account-JSON")],
            "Google Cloud DNS API"),
        Provider(
            "azure-dns",
            "Microsoft Azure DNS",
            Full,
            [
                Field("tenant_id", "Tenant-ID", true),
                Field("client_id", "Client-ID", true),
                Field("subscription_id", "Subscription-ID", true),
                Field("resource_group", "Resource Group", true),
            ],
            [Secret("client_secret", "Client Secret")],
            "Azure Resource Manager DNS API"),
        Provider(
            "vultr",
            "Vultr",
            Full,
            [],
            [Secret("api_key", "API-Key")],
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
            [Secret("api_key", "API-Key"), Secret("api_secret", "API-Secret")],
            "GoDaddy Domains API"),
        Provider(
            "duckdns",
            "DuckDNS",
            DnsProviderCapability.RecordManagement | DnsProviderCapability.DnsChallenge | DnsProviderCapability.DynamicDns,
            [Field("domain", "DuckDNS-Subdomain", true)],
            [Secret("token", "Token")],
            "DuckDNS Update API"),
        Provider(
            "rfc2136",
            "RFC 2136 / eigener DNS-Server",
            DnsProviderCapability.RecordManagement | DnsProviderCapability.DnsChallenge | DnsProviderCapability.DynamicDns,
            [
                Field("server", "DNS-Server", true),
                Field("key_name", "TSIG Key Name", true),
                Field("algorithm", "TSIG-Algorithmus", true, "hmac-sha256"),
            ],
            [Secret("shared_secret", "TSIG Shared Secret")],
            "Standardisierte dynamische DNS-Updates"),
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
        return new DnsProviderField(key, label, required, defaultValue);
    }

    private static DnsProviderField Secret(string key, string label)
    {
        return new DnsProviderField(
            key,
            label,
            Required: true,
            HelpText: "Name einer Umgebungsvariable oder Secret-Referenz; der Secret-Wert wird nicht gespeichert.");
    }
}
