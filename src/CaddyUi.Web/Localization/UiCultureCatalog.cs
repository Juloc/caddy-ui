using System.Globalization;

namespace CaddyUi.Web.Localization;

public sealed record UiCultureOption(string Name, string DisplayName, string NativeName);

/// <summary>
/// Defines the cultures offered by Caddy UI. Adding another language only requires
/// adding its culture code to configuration and a matching SharedResource resource file.
/// </summary>
public sealed class UiCultureCatalog
{
    public const string LanguageCookieName = "caddy-ui-language";
    public const string LanguageClaimType = "ui_culture";
    public const string FallbackCulture = "en";

    private readonly IReadOnlyDictionary<string, UiCultureOption> _cultures;

    public UiCultureCatalog(IConfiguration configuration)
    {
        var configured = configuration
            .GetSection("Localization:SupportedCultures")
            .Get<string[]>() ?? ["en", "de"];

        var cultures = new Dictionary<string, UiCultureOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in configured.Append(FallbackCulture))
        {
            var normalized = NormalizeName(value);
            if (cultures.ContainsKey(normalized))
            {
                continue;
            }

            var culture = CultureInfo.GetCultureInfo(normalized);
            cultures.Add(
                normalized,
                new UiCultureOption(
                    normalized,
                    culture.EnglishName,
                    culture.NativeName));
        }

        _cultures = cultures;
        DefaultCulture = Normalize(configuration["Localization:DefaultCulture"]);
    }

    public string DefaultCulture { get; }

    public IReadOnlyList<UiCultureOption> SupportedCultures =>
        _cultures.Values.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();

    public bool IsSupported(string? value)
    {
        return TryNormalize(value, out _);
    }

    public string Normalize(string? value)
    {
        return TryNormalize(value, out var normalized) ? normalized : FallbackCulture;
    }

    public bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var candidate = NormalizeName(value);
            if (_cultures.ContainsKey(candidate))
            {
                normalized = candidate;
                return true;
            }

            var neutral = CultureInfo.GetCultureInfo(candidate).TwoLetterISOLanguageName;
            if (_cultures.ContainsKey(neutral))
            {
                normalized = neutral;
                return true;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        return false;
    }

    private static string NormalizeName(string value)
    {
        return CultureInfo.GetCultureInfo(value.Trim()).Name.ToLowerInvariant();
    }
}
