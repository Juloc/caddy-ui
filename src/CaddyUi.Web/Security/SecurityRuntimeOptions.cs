using System.Net;

namespace CaddyUi.Web.Security;

public sealed class SecurityRuntimeOptions
{
    private SecurityRuntimeOptions(
        Uri? publicOrigin,
        string adminProxySecret,
        string portalProxySecret,
        bool requireTotp,
        string cookieNamespace)
    {
        PublicOrigin = publicOrigin;
        AdminProxySecret = adminProxySecret;
        PortalProxySecret = portalProxySecret;
        RequireTotp = requireTotp;
        CookieNamespace = cookieNamespace;
    }

    public Uri? PublicOrigin { get; }

    public string AdminProxySecret { get; }

    public string PortalProxySecret { get; }

    public bool RequireTotp { get; }

    public string CookieNamespace { get; }

    public string LanAdminCookieName => $"{CookieNamespace}_admin";

    public string PublicAdminCookieName => $"__Host-{CookieNamespace}_admin";

    public bool PublicAccessConfigured => PublicOrigin is not null;

    public bool PublicAccessWithoutMandatoryTotp => PublicAccessConfigured && !RequireTotp;

    public static SecurityRuntimeOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var originValue = configuration["CADDY_UI_PUBLIC_ORIGIN"] ??
            configuration["Security:PublicOrigin"];
        Uri? publicOrigin = null;
        if (!string.IsNullOrWhiteSpace(originValue))
        {
            if (!Uri.TryCreate(originValue, UriKind.Absolute, out publicOrigin) ||
                publicOrigin.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(publicOrigin.UserInfo) ||
                publicOrigin.Port != 443 ||
                publicOrigin.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(publicOrigin.Query) ||
                !string.IsNullOrEmpty(publicOrigin.Fragment))
            {
                throw new InvalidOperationException(
                    "CADDY_UI_PUBLIC_ORIGIN must be an HTTPS origin without credentials, custom port, path, query, or fragment.");
            }
        }

        var cookieNamespace = NormalizeCookieNamespace(
            configuration["CADDY_UI_COOKIE_NAMESPACE"] ??
            configuration["Security:CookieNamespace"] ??
            "caddy_ui");

        return new SecurityRuntimeOptions(
            publicOrigin,
            configuration["CADDY_UI_ADMIN_PROXY_SECRET"] ??
                configuration["Security:AdminProxySecret"] ??
                string.Empty,
            configuration["CADDY_UI_PORTAL_PROXY_SECRET"] ??
                configuration["Security:PortalProxySecret"] ??
                string.Empty,
            ParseBoolean(
                configuration["CADDY_UI_REQUIRE_TOTP"] ??
                configuration["Security:RequireTotp"]),
            cookieNamespace);
    }

    public static bool IsPrivateOrLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                bytes[0] == 127 ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            bytes[0] == 0xFC ||
            bytes[0] == 0xFD;
    }

    private static string NormalizeCookieNamespace(string value)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 40 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new InvalidOperationException(
                "CADDY_UI_COOKIE_NAMESPACE must contain 1 to 40 ASCII letters, digits, underscores, or hyphens.");
        }

        return value;
    }

    private static bool ParseBoolean(string? value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }
}
