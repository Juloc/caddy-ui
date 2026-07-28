using System.Globalization;
using CaddyUi.Domain.Certificates;

namespace CaddyUi.Domain.Domains;

public sealed record ManagedDomain
{
    private ManagedDomain(
        Guid id,
        string name,
        string displayName,
        CertificateMode defaultCertificateMode,
        bool enabled,
        bool isDefault,
        Guid? dnsProviderId)
    {
        Id = id;
        Name = name;
        DisplayName = displayName;
        DefaultCertificateMode = defaultCertificateMode;
        Enabled = enabled;
        IsDefault = isDefault;
        DnsProviderId = dnsProviderId;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string DisplayName { get; }

    public CertificateMode DefaultCertificateMode { get; }

    public bool Enabled { get; }

    public bool IsDefault { get; }

    public Guid? DnsProviderId { get; }

    public string WildcardHost => $"*.{Name}";

    public static ManagedDomain Create(
        string name,
        string? displayName = null,
        CertificateMode defaultCertificateMode = CertificateMode.Wildcard,
        bool enabled = true,
        bool isDefault = false,
        Guid? dnsProviderId = null,
        Guid? id = null)
    {
        var normalized = NormalizeName(name);
        if (defaultCertificateMode == CertificateMode.Inherit)
        {
            throw new ArgumentException(
                "A managed domain must define wildcard or individual certificates as its default.",
                nameof(defaultCertificateMode));
        }

        return new ManagedDomain(
            id ?? Guid.NewGuid(),
            normalized,
            string.IsNullOrWhiteSpace(displayName) ? normalized : displayName.Trim(),
            defaultCertificateMode,
            enabled,
            isDefault,
            dnsProviderId);
    }

    public CertificateMode ResolveCertificateMode(CertificateMode routeMode)
    {
        return routeMode == CertificateMode.Inherit
            ? DefaultCertificateMode
            : routeMode;
    }

    public bool WildcardCovers(string host)
    {
        var normalizedHost = NormalizeName(host);
        var suffix = $".{Name}";
        if (!normalizedHost.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var label = normalizedHost[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.');
    }

    public string HostForSubdomain(string? subdomain)
    {
        var normalizedSubdomain = (subdomain ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (normalizedSubdomain.Length == 0 || normalizedSubdomain == "@")
        {
            return Name;
        }

        if (normalizedSubdomain.Contains('*'))
        {
            throw new ArgumentException("Route subdomains cannot contain wildcard characters.", nameof(subdomain));
        }

        return NormalizeName($"{normalizedSubdomain}.{Name}");
    }

    public static string NormalizeName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var candidate = value.Trim().TrimEnd('.');
        if (candidate.StartsWith("*.", StringComparison.Ordinal))
        {
            candidate = candidate[2..];
        }

        if (candidate.Length == 0 || candidate.Contains('/') || candidate.Contains('\\'))
        {
            throw new ArgumentException("The domain name is invalid.", nameof(value));
        }

        var ascii = new IdnMapping().GetAscii(candidate).ToLowerInvariant();
        if (ascii.Length > 253)
        {
            throw new ArgumentException("The domain name is too long.", nameof(value));
        }

        var labels = ascii.Split('.');
        if (labels.Length < 2 || labels.Any(label =>
                label.Length is < 1 or > 63 ||
                label.StartsWith("-", StringComparison.Ordinal) ||
                label.EndsWith("-", StringComparison.Ordinal) ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            throw new ArgumentException("The domain name is invalid.", nameof(value));
        }

        return ascii;
    }
}
