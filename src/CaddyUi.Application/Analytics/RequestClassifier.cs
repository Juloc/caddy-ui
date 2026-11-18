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

        var requestType = ClassifyRequestType(request);
        var actorType = ClassifyActor(request, requestType);
        return new ClassifiedRequest(
            request,
            actorType,
            requestType);
    }

    private static AnalyticsRequestType ClassifyRequestType(
        NormalizedRequestEvent request)
    {
        if (request.Status == 101 ||
            request.Upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return AnalyticsRequestType.WebSocket;
        }

        if (IsHealthcheckPath(request.Path))
        {
            return AnalyticsRequestType.Healthcheck;
        }

        if (request.Path.StartsWith("/__caddy_ui_auth", StringComparison.OrdinalIgnoreCase))
        {
            return AnalyticsRequestType.Auth;
        }

        if (request.Path.StartsWith("/.well-known/acme-challenge/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Path, "/metrics", StringComparison.OrdinalIgnoreCase))
        {
            return AnalyticsRequestType.System;
        }

        if (IsExplicitApiPath(request.Path))
        {
            return AnalyticsRequestType.Api;
        }

        if (IsAssetPath(request.Path))
        {
            return AnalyticsRequestType.Asset;
        }

        if (IsApiByHeaders(request))
        {
            return AnalyticsRequestType.Api;
        }

        if (IsDocumentRequest(request))
        {
            return AnalyticsRequestType.Document;
        }

        return AnalyticsRequestType.Other;
    }

    private static AnalyticsActorType ClassifyActor(
        NormalizedRequestEvent request,
        AnalyticsRequestType requestType)
    {
        if ((requestType is AnalyticsRequestType.Healthcheck or AnalyticsRequestType.System) &&
            ContainsAny(request.UserAgent, InternalTokens))
        {
            return AnalyticsActorType.Internal;
        }

        if (ContainsAny(request.UserAgent, BotTokens))
        {
            return AnalyticsActorType.Bot;
        }

        if (request.SecFetchDest.Length > 0 ||
            ContainsAny(request.UserAgent, BrowserTokens))
        {
            return AnalyticsActorType.Human;
        }

        return AnalyticsActorType.Unknown;
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
