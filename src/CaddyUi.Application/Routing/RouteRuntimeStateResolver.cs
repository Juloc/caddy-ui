using System.Text.Json;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Routing;

public enum RouteRuntimeState
{
    Applied,
    ApplyRequired,
    NewDraft,
    PendingRemoval,
    Disabled,
    Unknown,
}

public sealed record AppliedRouteReference(
    Guid Id,
    string Name,
    string Host,
    string Kind,
    string Path);

public sealed record RoutingRuntimeState(
    string DesiredDigest,
    string ActiveDigest,
    Guid? ActiveRevisionId,
    bool ActiveStateTracked,
    bool HasPendingChanges,
    bool LastApplyFailed,
    string LastApplyError,
    IReadOnlyDictionary<Guid, RouteRuntimeState> RouteStates,
    IReadOnlyList<AppliedRouteReference> PendingRemovals)
{
    public RouteRuntimeState StateFor(Guid routeId)
    {
        return RouteStates.TryGetValue(routeId, out var state)
            ? state
            : RouteRuntimeState.Unknown;
    }

    public int ActiveRouteCount =>
        RouteStates.Count(item =>
            item.Value is RouteRuntimeState.Applied or
                RouteRuntimeState.ApplyRequired or
                RouteRuntimeState.PendingRemoval) +
        PendingRemovals.Count;
}

public static class RouteRuntimeStateResolver
{
    public static RoutingRuntimeState Resolve(
        IReadOnlyCollection<ManagedRouteDefinition> desiredRoutes,
        string desiredDigest,
        string desiredManifestJson,
        string activeDigest,
        Guid? activeRevisionId,
        string? activeManifestJson,
        bool activeContentIsEmpty,
        bool lastApplyFailed,
        string lastApplyError)
    {
        ArgumentNullException.ThrowIfNull(desiredRoutes);

        var desiredManifest = ParseManifest(desiredManifestJson);
        var activeManifest = ParseManifest(activeManifestJson);
        var activeStateTracked = activeContentIsEmpty || activeRevisionId is not null;
        var hasPendingChanges = !string.Equals(
            desiredDigest,
            activeDigest,
            StringComparison.OrdinalIgnoreCase);
        var desiredRouteIds = desiredRoutes
            .Select(route => route.Id)
            .ToHashSet();
        var activeGeneratedRoutes = activeManifest
            .Where(item => item.Value.Generated)
            .ToDictionary(item => item.Key, item => item.Value);
        var states = new Dictionary<Guid, RouteRuntimeState>();

        foreach (var route in desiredRoutes)
        {
            if (!activeStateTracked)
            {
                states[route.Id] = RouteRuntimeState.Unknown;
                continue;
            }

            if (!route.Enabled)
            {
                states[route.Id] = activeGeneratedRoutes.ContainsKey(route.Id)
                    ? RouteRuntimeState.PendingRemoval
                    : RouteRuntimeState.Disabled;
                continue;
            }

            if (!activeGeneratedRoutes.TryGetValue(route.Id, out var activeRoute))
            {
                states[route.Id] = RouteRuntimeState.NewDraft;
                continue;
            }

            if (!desiredManifest.TryGetValue(route.Id, out var desiredRoute) ||
                !desiredRoute.Generated)
            {
                states[route.Id] = RouteRuntimeState.ApplyRequired;
                continue;
            }

            if (desiredRoute.Fingerprint.Length > 0 && activeRoute.Fingerprint.Length > 0)
            {
                states[route.Id] = string.Equals(
                    desiredRoute.Fingerprint,
                    activeRoute.Fingerprint,
                    StringComparison.OrdinalIgnoreCase)
                    ? RouteRuntimeState.Applied
                    : RouteRuntimeState.ApplyRequired;
                continue;
            }

            states[route.Id] = hasPendingChanges
                ? RouteRuntimeState.ApplyRequired
                : RouteRuntimeState.Applied;
        }

        var pendingRemovals = activeStateTracked
            ? activeGeneratedRoutes.Values
                .Where(route => !desiredRouteIds.Contains(route.Id))
                .OrderBy(route => route.Host, StringComparer.Ordinal)
                .ThenBy(route => route.Path, StringComparer.Ordinal)
                .Select(route => new AppliedRouteReference(
                    route.Id,
                    route.Name,
                    route.Host,
                    route.Kind,
                    route.Path))
                .ToArray()
            : Array.Empty<AppliedRouteReference>();

        return new RoutingRuntimeState(
            desiredDigest,
            activeDigest,
            activeRevisionId,
            activeStateTracked,
            hasPendingChanges,
            lastApplyFailed && hasPendingChanges,
            lastApplyError,
            states,
            pendingRemovals);
    }

    private static IReadOnlyDictionary<Guid, ManifestRoute> ParseManifest(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return new Dictionary<Guid, ManifestRoute>();
        }

        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (!document.RootElement.TryGetProperty("routes", out var routes) ||
                routes.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<Guid, ManifestRoute>();
            }

            var result = new Dictionary<Guid, ManifestRoute>();
            foreach (var route in routes.EnumerateArray())
            {
                if (!route.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(idProperty.GetString(), out var id))
                {
                    continue;
                }

                result[id] = new ManifestRoute(
                    id,
                    String(route, "name"),
                    String(route, "host"),
                    String(route, "kind"),
                    String(route, "path"),
                    String(route, "fingerprint"),
                    Boolean(route, "generated", defaultValue: true));
            }

            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<Guid, ManifestRoute>();
        }
    }

    private static string String(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool Boolean(
        JsonElement element,
        string propertyName,
        bool defaultValue)
    {
        return TryGetProperty(element, propertyName, out var property) &&
               property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : defaultValue;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement property)
    {
        if (element.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(
                    candidate.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private sealed record ManifestRoute(
        Guid Id,
        string Name,
        string Host,
        string Kind,
        string Path,
        string Fingerprint,
        bool Generated);
}
