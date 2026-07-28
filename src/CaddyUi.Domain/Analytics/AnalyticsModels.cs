namespace CaddyUi.Domain.Analytics;

public enum AnalyticsActorType
{
    Human,
    Bot,
    Internal,
    Unknown,
}

public enum AnalyticsRequestType
{
    Document,
    Asset,
    Api,
    WebSocket,
    Healthcheck,
    Auth,
    System,
    Other,
}

public enum AnalyticsClassificationConfidence
{
    High,
    Medium,
    Low,
}

public enum AnalyticsNavigationState
{
    Succeeded,
    Redirected,
    Failed,
}

public sealed record NormalizedRequestEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    string SourceFile,
    long SourceOffset,
    string Host,
    string Method,
    string Path,
    string QueryString,
    int Status,
    double DurationMilliseconds,
    long BytesSent,
    string? RemoteAddress,
    string UserAgent,
    string Referer,
    string AcceptHeader,
    string ContentType,
    string SecFetchDest,
    string Upgrade,
    string? FirstPartyClientIdentifier,
    string RawJson);

public sealed record ClassifiedRequest(
    NormalizedRequestEvent Request,
    AnalyticsActorType ActorType,
    AnalyticsRequestType RequestType,
    AnalyticsClassificationConfidence Confidence,
    bool IsNavigation,
    bool IsPageView,
    AnalyticsNavigationState NavigationState,
    IReadOnlyList<string> Evidence);

public static class AnalyticsStorageValues
{
    public static string ToStorageValue(this AnalyticsActorType value)
    {
        return value switch
        {
            AnalyticsActorType.Human => "human",
            AnalyticsActorType.Bot => "bot",
            AnalyticsActorType.Internal => "internal",
            AnalyticsActorType.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    public static string ToStorageValue(this AnalyticsRequestType value)
    {
        return value switch
        {
            AnalyticsRequestType.Document => "document",
            AnalyticsRequestType.Asset => "asset",
            AnalyticsRequestType.Api => "api",
            AnalyticsRequestType.WebSocket => "websocket",
            AnalyticsRequestType.Healthcheck => "healthcheck",
            AnalyticsRequestType.Auth => "auth",
            AnalyticsRequestType.System => "system",
            AnalyticsRequestType.Other => "other",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    public static string ToStorageValue(this AnalyticsClassificationConfidence value)
    {
        return value switch
        {
            AnalyticsClassificationConfidence.High => "high",
            AnalyticsClassificationConfidence.Medium => "medium",
            AnalyticsClassificationConfidence.Low => "low",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    public static string ToStorageValue(this AnalyticsNavigationState value)
    {
        return value switch
        {
            AnalyticsNavigationState.Succeeded => "succeeded",
            AnalyticsNavigationState.Redirected => "redirected",
            AnalyticsNavigationState.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }
}
