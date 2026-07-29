using Microsoft.Extensions.Configuration;

namespace CaddyUi.Infrastructure.Routing;

public enum RouteWriteMode
{
    Disabled,
    Shadow,
    Active,
}

public sealed class RoutingOptions
{
    public RouteWriteMode WriteMode { get; init; } = RouteWriteMode.Disabled;

    public string ManagedFragmentPath { get; init; } = "/data/caddy-ui/generated/managed-routes.caddy";

    public string ShadowFragmentPath { get; init; } = "/data/caddy-ui/shadow/managed-routes.caddy";

    public string RootConfigPath { get; init; } = "/etc/caddy/Caddyfile";

    public string CaddyBinaryPath { get; init; } = "/usr/bin/caddy";

    public string PortalUpstream { get; init; } = "127.0.0.1:8099";

    public int CommandTimeoutSeconds { get; init; } = 30;

    public bool AllowCustomRoutes { get; init; }

    public static RoutingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Routing");
        return new RoutingOptions
        {
            WriteMode = ParseMode(
                configuration["CADDY_UI_ROUTE_WRITE_MODE"] ?? section["WriteMode"]),
            ManagedFragmentPath = Value(
                configuration["CADDY_UI_MANAGED_ROUTES_PATH"] ?? section["ManagedFragmentPath"],
                "/data/caddy-ui/generated/managed-routes.caddy"),
            ShadowFragmentPath = Value(
                configuration["CADDY_UI_ROUTE_SHADOW_PATH"] ?? section["ShadowFragmentPath"],
                "/data/caddy-ui/shadow/managed-routes.caddy"),
            RootConfigPath = Value(
                configuration["CADDY_UI_CADDY_ROOT_CONFIG"] ?? section["RootConfigPath"],
                "/etc/caddy/Caddyfile"),
            CaddyBinaryPath = Value(
                configuration["CADDY_UI_CADDY_BINARY"] ?? section["CaddyBinaryPath"],
                "/usr/bin/caddy"),
            PortalUpstream = Value(
                configuration["CADDY_UI_PORTAL_UPSTREAM"] ?? section["PortalUpstream"],
                "127.0.0.1:8099"),
            CommandTimeoutSeconds = Math.Clamp(
                ParseInt(configuration["CADDY_UI_CADDY_COMMAND_TIMEOUT_SECONDS"] ?? section["CommandTimeoutSeconds"], 30),
                5,
                300),
            AllowCustomRoutes = ParseBool(
                configuration["CADDY_UI_ALLOW_CUSTOM_ROUTES"] ?? section["AllowCustomRoutes"]),
        };
    }

    private static RouteWriteMode ParseMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "shadow" => RouteWriteMode.Shadow,
            "active" => RouteWriteMode.Active,
            _ => RouteWriteMode.Disabled,
        };
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var result) ? result : fallback;
    }

    private static bool ParseBool(string? value)
    {
        return bool.TryParse(value, out var result) && result;
    }

    private static string Value(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
