using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CaddyUi.Web.Security;

public enum RequestSurface
{
    Rejected = 0,
    Lan = 1,
    PublicAdmin = 2,
    Portal = 3,
}

public sealed class RequestSurfaceResolver
{
    public const string SurfaceItemKey = "CaddyUi.RequestSurface";
    public const string AdminProxyHeader = "X-Caddy-Admin-Secret";
    public const string PortalProxyHeader = "X-Caddy-Portal-Secret";

    private readonly SecurityRuntimeOptions _options;

    public RequestSurfaceResolver(SecurityRuntimeOptions options)
    {
        _options = options;
    }

    public RequestSurface Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Connection.LocalPort == 8099)
        {
            return IsValidProxyRequest(context, PortalProxyHeader, _options.PortalProxySecret, expectedHost: null)
                ? RequestSurface.Portal
                : RequestSurface.Rejected;
        }

        if (_options.PublicOrigin is not null &&
            IsValidProxyRequest(
                context,
                AdminProxyHeader,
                _options.AdminProxySecret,
                _options.PublicOrigin.Host))
        {
            return RequestSurface.PublicAdmin;
        }

        return SecurityRuntimeOptions.IsPrivateOrLoopback(context.Connection.RemoteIpAddress) &&
            IsPrivateHost(context.Request.Host.Host)
                ? RequestSurface.Lan
                : RequestSurface.Rejected;
    }

    public RequestSurface GetResolved(HttpContext context)
    {
        return context.Items.TryGetValue(SurfaceItemKey, out var value) && value is RequestSurface surface
            ? surface
            : Resolve(context);
    }

    public string GetClientAddress(HttpContext context)
    {
        var surface = GetResolved(context);
        if (surface is RequestSurface.PublicAdmin or RequestSurface.Portal &&
            SecurityRuntimeOptions.IsPrivateOrLoopback(context.Connection.RemoteIpAddress))
        {
            var forwarded = FirstHeaderValue(context.Request.Headers["X-Forwarded-For"]);
            if (IPAddress.TryParse(forwarded, out var address))
            {
                return address.ToString();
            }
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    public bool IsOriginAllowed(HttpContext context)
    {
        var surface = GetResolved(context);
        if (surface == RequestSurface.Rejected)
        {
            return false;
        }

        if (string.Equals(
                context.Request.Headers["Sec-Fetch-Site"],
                "cross-site",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var origin = FirstHeaderValue(context.Request.Headers.Origin);
        if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expected = ExpectedOrigin(context, surface);
        if (!string.IsNullOrWhiteSpace(origin))
        {
            return SameOrigin(origin, expected);
        }

        var referer = FirstHeaderValue(context.Request.Headers.Referer);
        if (!string.IsNullOrWhiteSpace(referer))
        {
            return SameOrigin(referer, expected);
        }

        return surface == RequestSurface.Lan;
    }

    private bool IsValidProxyRequest(
        HttpContext context,
        string headerName,
        string configuredSecret,
        string? expectedHost)
    {
        if (string.IsNullOrWhiteSpace(configuredSecret) ||
            !SecurityRuntimeOptions.IsPrivateOrLoopback(context.Connection.RemoteIpAddress) ||
            !FixedTimeEquals(context.Request.Headers[headerName], configuredSecret) ||
            !string.Equals(
                FirstHeaderValue(context.Request.Headers["X-Forwarded-Proto"]),
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (expectedHost is null)
        {
            return !string.IsNullOrWhiteSpace(
                FirstHeaderValue(context.Request.Headers["X-Forwarded-Host"]));
        }

        var forwardedHost = FirstHeaderValue(context.Request.Headers["X-Forwarded-Host"]);
        return string.Equals(
            RemovePort(forwardedHost),
            expectedHost,
            StringComparison.OrdinalIgnoreCase);
    }

    private string ExpectedOrigin(HttpContext context, RequestSurface surface)
    {
        if (surface == RequestSurface.PublicAdmin && _options.PublicOrigin is not null)
        {
            return _options.PublicOrigin.GetLeftPart(UriPartial.Authority);
        }

        if (surface == RequestSurface.Portal)
        {
            return $"https://{FirstHeaderValue(context.Request.Headers["X-Forwarded-Host"])}";
        }

        return $"{context.Request.Scheme}://{context.Request.Host.Value}";
    }

    private static bool IsPrivateHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) &&
            SecurityRuntimeOptions.IsPrivateOrLoopback(address);
    }

    private static bool SameOrigin(string candidate, string expected)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri) ||
            !Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri))
        {
            return false;
        }

        return string.Equals(
            candidateUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            expectedUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstHeaderValue(string? value)
    {
        return (value ?? string.Empty).Split(',', 2)[0].Trim();
    }

    private static string RemovePort(string value)
    {
        return Uri.TryCreate($"https://{value}", UriKind.Absolute, out var uri)
            ? uri.Host
            : value;
    }

    private static bool FixedTimeEquals(string? supplied, string expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
