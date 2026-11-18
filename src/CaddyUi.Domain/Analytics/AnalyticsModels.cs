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

public sealed record NormalizedRequestEvent(
    DateTimeOffset OccurredAt,
    string SourceFile,
    long SourceOffset,
    string Host,
    string Method,
    string Path,
    int Status,
    double DurationMilliseconds,
    long BytesSent,
    string? RemoteAddress,
    string UserAgent,
    string AcceptHeader,
    string ContentType,
    string SecFetchDest,
    string Upgrade,
    string? FirstPartyClientIdentifier);

public sealed record ClassifiedRequest(
    NormalizedRequestEvent Request,
    AnalyticsActorType ActorType,
    AnalyticsRequestType RequestType);

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


}
