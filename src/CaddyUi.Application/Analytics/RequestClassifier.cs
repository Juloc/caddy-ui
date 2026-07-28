using CaddyUi.Domain.Analytics;

namespace CaddyUi.Application.Analytics;

public sealed class RequestClassifier
{
    private static readonly string[] BotTokens =
    [
        "bot",
        "crawler",
        "spider",
        "slurp",
        "scanner",
        "python-requests",
        "go-http-client",
        "wget/",
        "curl/",
        "headless",
        "phantomjs",
        "selenium",
        "playwright",
        "uptimerobot",
    ];

    private static readonly string[] InternalTokens =
    [
        "kube-probe",
        "docker-healthcheck",
        "caddy-health",
        "healthcheck",
        "prometheus",
    ];

    private static readonly string[] BrowserTokens =
    [
        "mozilla/",
        "chrome/",
        "chromium/",
        "safari/",
        "firefox/",
        "edg/",
        "opr/",
    ];

    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif",
        ".bmp",
        ".css",
        ".eot",
        ".gif",
        ".ico",
        ".jpeg",
        ".jpg",
        ".js",
        ".json",
        ".map",
        ".mjs",
        ".mp3",
        ".mp4",
        ".ogg",
        ".otf",
        ".pdf",
        ".png",
        ".svg",
        ".ttf",
        ".webmanifest",
        ".webp",
        ".woff",
        ".woff2",
        ".xml",
    };

    public ClassifiedRequest Classify(NormalizedRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var evidence = new List<string>();
        var requestType = ClassifyRequestType(request, evidence);
        var actorType = ClassifyActor(request, requestType, evidence);
        var confidence = ClassifyConfidence(request, requestType, actorType);
        var isNavigation =
            requestType == AnalyticsRequestType.Document &&
            actorType is AnalyticsActorType.Human or AnalyticsActorType.Unknown &&
            request.Method is "GET" or "HEAD";
        var isPageView =
            isNavigation &&
            (request.Status is >= 200 and < 300 || request.Status == 304);
        var navigationState = request.Status switch
        {
            304 => AnalyticsNavigationState.Succeeded,
            >= 300 and < 400 => AnalyticsNavigationState.Redirected,
            >= 400 => AnalyticsNavigationState.Failed,
            _ => AnalyticsNavigationState.Succeeded,
        };

        if (isNavigation)
        {
            evidence.Add("navigation-candidate");
        }

        if (isPageView)
        {
            evidence.Add("successful-document-response");
        }

        return new ClassifiedRequest(
            request,
            actorType,
            requestType,
            confidence,
            isNavigation,
            isPageView,
            navigationState,
            evidence);
    }

    private static AnalyticsRequestType ClassifyRequestType(
        NormalizedRequestEvent request,
        ICollection<string> evidence)
    {
        if (request.Status == 101 ||
            request.Upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add("websocket-upgrade");
            return AnalyticsRequestType.WebSocket;
        }

        if (IsHealthcheckPath(request.Path))
        {
            evidence.Add("healthcheck-path");
            return AnalyticsRequestType.Healthcheck;
        }

        if (request.Path.StartsWith("/__caddy_ui_auth", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add("caddy-ui-auth-path");
            return AnalyticsRequestType.Auth;
        }

        if (request.Path.StartsWith("/.well-known/acme-challenge/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Path, "/metrics", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add("system-path");
            return AnalyticsRequestType.System;
        }

        if (IsExplicitApiPath(request.Path))
        {
            evidence.Add("api-path");
            return AnalyticsRequestType.Api;
        }

        if (IsAssetPath(request.Path))
        {
            evidence.Add("asset-path");
            return AnalyticsRequestType.Asset;
        }

        if (IsApiByHeaders(request))
        {
            evidence.Add("api-header-evidence");
            return AnalyticsRequestType.Api;
        }

        if (IsDocumentRequest(request))
        {
            evidence.Add("document-evidence");
            return AnalyticsRequestType.Document;
        }

        evidence.Add("no-specific-resource-evidence");
        return AnalyticsRequestType.Other;
    }

    private static AnalyticsActorType ClassifyActor(
        NormalizedRequestEvent request,
        AnalyticsRequestType requestType,
        ICollection<string> evidence)
    {
        if ((requestType is AnalyticsRequestType.Healthcheck or AnalyticsRequestType.System) &&
            ContainsAny(request.UserAgent, InternalTokens))
        {
            evidence.Add("internal-user-agent");
            return AnalyticsActorType.Internal;
        }

        if (ContainsAny(request.UserAgent, BotTokens))
        {
            evidence.Add("automation-user-agent");
            return AnalyticsActorType.Bot;
        }

        if (request.SecFetchDest.Length > 0 ||
            ContainsAny(request.UserAgent, BrowserTokens))
        {
            evidence.Add("browser-evidence");
            return AnalyticsActorType.Human;
        }

        evidence.Add("actor-unknown");
        return AnalyticsActorType.Unknown;
    }

    private static AnalyticsClassificationConfidence ClassifyConfidence(
        NormalizedRequestEvent request,
        AnalyticsRequestType requestType,
        AnalyticsActorType actorType)
    {
        if (request.Status == 101 ||
            request.SecFetchDest.Length > 0 ||
            (requestType is AnalyticsRequestType.Healthcheck or
                AnalyticsRequestType.Auth or
                AnalyticsRequestType.System) ||
            IsAssetPath(request.Path) ||
            request.Path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            request.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
            request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return AnalyticsClassificationConfidence.High;
        }

        if (request.AcceptHeader.Length > 0 ||
            actorType != AnalyticsActorType.Unknown)
        {
            return AnalyticsClassificationConfidence.Medium;
        }

        return AnalyticsClassificationConfidence.Low;
    }

    private static bool IsDocumentRequest(NormalizedRequestEvent request)
    {
        if (request.Method is not ("GET" or "HEAD"))
        {
            return false;
        }

        return string.Equals(request.SecFetchDest, "document", StringComparison.OrdinalIgnoreCase) ||
            request.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
            request.AcceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitApiPath(string path)
    {
        return string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/graphql", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/graphql/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApiByHeaders(NormalizedRequestEvent request)
    {
        return request.ContentType.Contains(
                "application/json",
                StringComparison.OrdinalIgnoreCase) ||
            request.AcceptHeader.Contains(
                "application/json",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHealthcheckPath(string path)
    {
        return string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/healthz", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/ready", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/readyz", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/live", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/livez", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAssetPath(string path)
    {
        if (path.StartsWith("/_nuxt/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_next/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/static/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lastSlash = path.LastIndexOf('/');
        var lastDot = path.LastIndexOf('.');
        if (lastDot <= lastSlash)
        {
            return false;
        }

        return AssetExtensions.Contains(path[lastDot..]);
    }

    private static bool ContainsAny(string value, IEnumerable<string> candidates)
    {
        return candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
