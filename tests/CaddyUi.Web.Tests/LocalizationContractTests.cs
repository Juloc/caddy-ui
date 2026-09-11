using System.Xml.Linq;
using CaddyUi.Web.Localization;
using Microsoft.Extensions.Configuration;

namespace CaddyUi.Web.Tests;

public sealed class LocalizationContractTests
{
    [Fact]
    public void DefaultLanguagePolicy_IsGermanWithEnglishSourceFallback()
    {
        var settings = ReadRepositoryFile("src/CaddyUi.Web/appsettings.json");
        var catalog = ReadRepositoryFile("src/CaddyUi.Web/Localization/UiCultureCatalog.cs");
        var guide = ReadRepositoryFile("AGENTS.md");
        var contract = ReadRepositoryFile("docs/MULTILINGUAL_UI.md");

        Assert.Contains("\"DefaultCulture\": \"de\"", settings, StringComparison.Ordinal);
        Assert.Contains("\"en\"", settings, StringComparison.Ordinal);
        Assert.Contains("\"de\"", settings, StringComparison.Ordinal);
        Assert.Contains("FallbackCulture = \"en\"", catalog, StringComparison.Ordinal);
        Assert.Contains(
            "German as the configured default and English as the neutral source-key fallback",
            guide,
            StringComparison.Ordinal);
        Assert.Contains(
            "The configured product default is German.",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "English remains the neutral source language for localization keys.",
            contract,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "de")]
    [InlineData("", "de")]
    [InlineData("unsupported", "de")]
    [InlineData("de-DE", "de")]
    [InlineData("en-US", "en")]
    public void ResolvePreference_UsesConfiguredDefaultForMissingOrUnsupportedValues(
        string? preference,
        string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Localization:DefaultCulture"] = "de",
                    ["Localization:SupportedCultures:0"] = "en",
                    ["Localization:SupportedCultures:1"] = "de",
                })
            .Build();
        var catalog = new UiCultureCatalog(configuration);

        Assert.Equal("de", catalog.DefaultCulture);
        Assert.Equal(expected, catalog.ResolvePreference(preference));
    }

    [Fact]
    public void LoginSettingsAndPersistence_DoNotInventEnglishPreference()
    {
        var loginModel = ReadRepositoryFile("src/CaddyUi.Web/Pages/Login.cshtml.cs");
        var settingsModel = ReadRepositoryFile("src/CaddyUi.Web/Pages/Settings/Index.cshtml.cs");
        var middleware = ReadRepositoryFile("src/CaddyUi.Web/Localization/UserCultureMiddleware.cs");
        var preferenceStore = ReadRepositoryFile("src/CaddyUi.Infrastructure/Security/UserPreferenceStore.cs");
        var page = ReadRepositoryFile("src/CaddyUi.Web/Pages/Settings/Index.cshtml");

        Assert.Contains("_cultures.ResolvePreference(", loginModel, StringComparison.Ordinal);
        Assert.Contains("_cultures.ResolvePreference(", settingsModel, StringComparison.Ordinal);
        Assert.Contains("catalog.ResolvePreference(requested)", middleware, StringComparison.Ordinal);
        Assert.DoesNotContain("_cultures.Normalize(", loginModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_cultures.Normalize(", settingsModel, StringComparison.Ordinal);
        Assert.Contains("Task<string?> GetLanguageAsync", preferenceStore, StringComparison.Ordinal);
        Assert.Contains("? language\n            : null;", preferenceStore, StringComparison.Ordinal);
        Assert.DoesNotContain("? language\n            : \"en\";", preferenceStore, StringComparison.Ordinal);
        Assert.Contains("IStringLocalizer<CaddyUi.Web.SettingsResource>", page, StringComparison.Ordinal);
        Assert.Contains(
            "German is the default. The preference is stored with your user account.",
            page,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseDefaultCulture_IsGermanWithoutRewritingExistingPreferences()
    {
        var migration = ReadRepositoryFile(
            "src/CaddyUi.Infrastructure/Persistence/Migrations/20260911070000_GermanDefaultCulture.cs");

        Assert.Contains("ALTER COLUMN language SET DEFAULT 'de'", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE caddy_ui.users", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("German is the product default", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureResources_ProvideGermanTranslationsForAuditedSurfaces()
    {
        var routingKeys = ResourceKeys("src/CaddyUi.Web/Resources/RoutingResource.de.resx");
        var settingsKeys = ResourceKeys("src/CaddyUi.Web/Resources/SettingsResource.de.resx");

        Assert.Contains(
            "Manage services by domain. New standard routes only need a name and an upstream target.",
            routingKeys);
        Assert.Contains("Preview and apply", routingKeys);
        Assert.Contains("Removal pending", routingKeys);
        Assert.Contains("Create and activate", routingKeys);
        Assert.Contains(
            "German is the default. The preference is stored with your user account.",
            settingsKeys);
    }

    private static HashSet<string> ResourceKeys(string relativePath)
    {
        var resource = XDocument.Parse(ReadRepositoryFile(relativePath));
        return resource.Root!
            .Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CaddyUi.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(
            Path.Combine(directory!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
