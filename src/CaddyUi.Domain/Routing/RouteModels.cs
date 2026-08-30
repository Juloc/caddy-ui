using System.Text.RegularExpressions;

namespace CaddyUi.Domain.Routing;

public enum ManagedRouteKind
{
    Proxy,
    Redirect,
    StaticResponse,
    Custom,
}

public enum RouteCertificateMode
{
    Inherit,
    Wildcard,
    Individual,
}

public sealed record RouteConfigurationDocument(
    string Schema,
    string PathPrefix,
    string Upstream,
    bool PreserveHost,
    string HealthPath,
    int HealthIntervalSeconds,
    string RedirectTarget,
    bool RedirectPermanent,
    int StaticStatusCode,
    string StaticBody,
    string CustomSnippet,
    bool SkipUpstreamTlsVerification = false)
{
    public static RouteConfigurationDocument Empty { get; } = new(
        "route-v1",
        "/",
        string.Empty,
        false,
        string.Empty,
        30,
        string.Empty,
        true,
        200,
        string.Empty,
        string.Empty,
        false);
}

public sealed partial record ManagedRouteDefinition(
    Guid Id,
    string Name,
    Guid DomainId,
    string DomainName,
    string Subdomain,
    string Host,
    ManagedRouteKind Kind,
    bool Enabled,
    int SortOrder,
    RouteCertificateMode CertificateMode,
    Guid? AccessGroupId,
    RouteConfigurationDocument Configuration)
{
    public static ManagedRouteDefinition Create(
        Guid id,
        string name,
        Guid domainId,
        string domainName,
        string? subdomain,
        ManagedRouteKind kind,
        bool enabled,
        int sortOrder,
        RouteCertificateMode certificateMode,
        Guid? accessGroupId,
        RouteConfigurationDocument configuration)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A route ID is required.", nameof(id));
        }

        if (domainId == Guid.Empty)
        {
            throw new ArgumentException("A managed domain is required.", nameof(domainId));
        }

        ArgumentNullException.ThrowIfNull(configuration);
        var normalizedName = Required(name, 120, "Route name");
        var normalizedDomain = NormalizeHost(domainName, "Domain");
        var normalizedSubdomain = NormalizeSubdomain(subdomain);
        var host = normalizedSubdomain.Length == 0
            ? normalizedDomain
            : $"{normalizedSubdomain}.{normalizedDomain}";
        var normalizedConfiguration = NormalizeConfiguration(kind, configuration);

        return new ManagedRouteDefinition(
            id,
            normalizedName,
            domainId,
            normalizedDomain,
            normalizedSubdomain,
            host,
            kind,
            enabled,
            Math.Clamp(sortOrder, -10_000, 10_000),
            certificateMode,
            accessGroupId,
            normalizedConfiguration);
    }

    public static string ToStorageValue(ManagedRouteKind kind)
    {
        return kind switch
        {
            ManagedRouteKind.Proxy => "proxy",
            ManagedRouteKind.Redirect => "redirect",
            ManagedRouteKind.StaticResponse => "static",
            ManagedRouteKind.Custom => "custom",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public static ManagedRouteKind ParseKind(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "proxy" => ManagedRouteKind.Proxy,
            "redirect" => ManagedRouteKind.Redirect,
            "static" or "static_response" => ManagedRouteKind.StaticResponse,
            "custom" => ManagedRouteKind.Custom,
            _ => throw new ArgumentException("Unsupported route kind.", nameof(value)),
        };
    }

    public static string ToStorageValue(RouteCertificateMode mode)
    {
        return mode switch
        {
            RouteCertificateMode.Inherit => "inherit",
            RouteCertificateMode.Wildcard => "wildcard",
            RouteCertificateMode.Individual => "individual",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    public static RouteCertificateMode ParseCertificateMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "wildcard" => RouteCertificateMode.Wildcard,
            "individual" => RouteCertificateMode.Individual,
            _ => RouteCertificateMode.Inherit,
        };
    }

    private static RouteConfigurationDocument NormalizeConfiguration(
        ManagedRouteKind kind,
        RouteConfigurationDocument configuration)
    {
        var pathPrefix = NormalizePath(configuration.PathPrefix);
        var healthPath = string.IsNullOrWhiteSpace(configuration.HealthPath)
            ? string.Empty
            : NormalizePath(configuration.HealthPath);
        var healthInterval = Math.Clamp(configuration.HealthIntervalSeconds, 5, 3600);
        var staticStatus = Math.Clamp(configuration.StaticStatusCode, 100, 599);
        var staticBody = Bounded(configuration.StaticBody ?? string.Empty, 64_000, "Static body");
        var customSnippet = Bounded(
            configuration.CustomSnippet ?? string.Empty,
            64_000,
            "Custom snippet").Trim();

        var upstream = string.Empty;
        var redirectTarget = string.Empty;
        switch (kind)
        {
            case ManagedRouteKind.Proxy:
                upstream = NormalizeUpstream(configuration.Upstream);
                break;
            case ManagedRouteKind.Redirect:
                redirectTarget = NormalizeRedirect(configuration.RedirectTarget);
                break;
            case ManagedRouteKind.StaticResponse:
                break;
            case ManagedRouteKind.Custom:
                if (customSnippet.Length == 0)
                {
                    throw new ArgumentException("A custom route requires a Caddyfile snippet.", nameof(configuration));
                }

                if (customSnippet.Contains('\0'))
                {
                    throw new ArgumentException("The custom route contains invalid characters.", nameof(configuration));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (configuration.SkipUpstreamTlsVerification &&
            (kind != ManagedRouteKind.Proxy ||
             !upstream.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Skipping upstream TLS verification requires an HTTPS proxy upstream.",
                nameof(configuration));
        }

        return configuration with
        {
            Schema = "route-v1",
            PathPrefix = pathPrefix,
            Upstream = upstream,
            HealthPath = healthPath,
            HealthIntervalSeconds = healthInterval,
            RedirectTarget = redirectTarget,
            StaticStatusCode = staticStatus,
            StaticBody = staticBody,
            CustomSnippet = customSnippet,
            SkipUpstreamTlsVerification =
                kind == ManagedRouteKind.Proxy && configuration.SkipUpstreamTlsVerification,
        };
    }

    private static string NormalizeHost(string? value, string description)
    {
        var host = Required(value, 253, description)
            .TrimEnd('.')
            .ToLowerInvariant();
        if (!HostPattern().IsMatch(host) || host.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{description} is not a valid DNS host name.", nameof(value));
        }

        return host;
    }

    private static string NormalizeSubdomain(string? value)
    {
        var candidate = value?.Trim().Trim('.').ToLowerInvariant() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        if (candidate.Length > 190 || candidate.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("The subdomain is invalid.", nameof(value));
        }

        if (candidate == "*")
        {
            return candidate;
        }

        if (candidate.StartsWith("*.", StringComparison.Ordinal))
        {
            var wildcardSuffix = candidate[2..];
            if (wildcardSuffix.Length == 0 ||
                wildcardSuffix.Contains('*', StringComparison.Ordinal) ||
                !HostPattern().IsMatch(wildcardSuffix))
            {
                throw new ArgumentException("The subdomain is invalid.", nameof(value));
            }

            return candidate;
        }

        if (candidate.Contains('*', StringComparison.Ordinal) || !HostPattern().IsMatch(candidate))
        {
            throw new ArgumentException("The subdomain is invalid.", nameof(value));
        }

        return candidate;
    }

    private static string NormalizePath(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        if (!path.StartsWith("/", StringComparison.Ordinal) ||
            path.Contains('\r') ||
            path.Contains('\n') ||
            path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path prefixes must start with '/' and must not contain traversal or line breaks.", nameof(value));
        }

        if (path.Length > 1024)
        {
            throw new ArgumentException("The path prefix is too long.", nameof(value));
        }

        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string NormalizeUpstream(string? value)
    {
        var upstream = Required(value, 2048, "Upstream");
        RejectCaddyfileInjection(upstream, nameof(value));
        if (upstream.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            upstream.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri) ||
                uri.Host.Length == 0 ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("The upstream URL is invalid.", nameof(value));
            }

            return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        }

        if (!HostPortPattern().IsMatch(upstream))
        {
            throw new ArgumentException("The upstream must be host:port or an HTTP(S) URL.", nameof(value));
        }

        var portText = upstream[(upstream.LastIndexOf(':') + 1)..];
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65_535)
        {
            throw new ArgumentException("The upstream port must be between 1 and 65535.", nameof(value));
        }

        return upstream.ToLowerInvariant();
    }

    private static string NormalizeRedirect(string? value)
    {
        var target = Required(value, 4096, "Redirect target");
        RejectCaddyfileInjection(target, nameof(value));
        if (target.StartsWith("/", StringComparison.Ordinal) &&
            !target.StartsWith("//", StringComparison.Ordinal))
        {
            return target;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The redirect target must be a local path or an HTTP(S) URL.", nameof(value));
        }

        return uri.AbsoluteUri;
    }

    private static void RejectCaddyfileInjection(string value, string parameterName)
    {
        if (value.Contains('\r') || value.Contains('\n') || value.Contains('{') || value.Contains('}'))
        {
            throw new ArgumentException(
                "The value contains characters that are not allowed in generated Caddy configuration.",
                parameterName);
        }
    }

    private static string Required(string? value, int maximum, string description)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            throw new ArgumentException($"{description} is required.", nameof(value));
        }

        return Bounded(candidate, maximum, description);
    }

    private static string Bounded(string value, int maximum, string description)
    {
        if (value.Length > maximum)
        {
            throw new ArgumentException($"{description} must not exceed {maximum} characters.", nameof(value));
        }

        return value;
    }

    [GeneratedRegex("^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\\.(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?))*$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex("^(?:[a-zA-Z0-9._-]+|\\[[0-9a-fA-F:]+\\]):[0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPortPattern();
}
