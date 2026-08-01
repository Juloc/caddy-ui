using System.Text.Json;

namespace CaddyUi.Infrastructure.Routing;

public sealed record AccessGroupPresentation(string AccentColor, string IconUrl)
{
    public const string DefaultAccentColor = "#0F6CBD";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public string EffectiveAccentColor =>
        string.IsNullOrWhiteSpace(AccentColor) ? DefaultAccentColor : AccentColor;

    public static AccessGroupPresentation Create(
        string? accentColor,
        string? iconUrl)
    {
        return new AccessGroupPresentation(
            NormalizeAccentColor(accentColor),
            NormalizeIconUrl(iconUrl));
    }

    public static AccessGroupPresentation FromJson(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return Create(null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Create(null, null);
            }

            var accentColor = document.RootElement.TryGetProperty(
                "accentColor",
                out var accentElement)
                    ? accentElement.GetString()
                    : null;
            var iconUrl = document.RootElement.TryGetProperty(
                "iconUrl",
                out var iconElement)
                    ? iconElement.GetString()
                    : null;
            return Create(accentColor, iconUrl);
        }
        catch (JsonException)
        {
            return Create(null, null);
        }
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(
            new
            {
                accentColor = AccentColor,
                iconUrl = IconUrl,
            },
            JsonOptions);
    }

    private static string NormalizeAccentColor(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        if (candidate.Length != 7 ||
            candidate[0] != '#' ||
            !candidate.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Accent color must be empty or use the hexadecimal format #RRGGBB.");
        }

        return candidate.ToUpperInvariant();
    }

    private static string NormalizeIconUrl(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        if (candidate.Length > 2048 ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Icon URL must be empty or an absolute HTTPS URL.");
        }

        return uri.AbsoluteUri;
    }
}
