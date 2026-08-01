using System.Text.RegularExpressions;

namespace CaddyUi.Web.Tests;

public sealed class FluentDesignContractTests
{
    private static readonly Regex StylesheetReference = new(
        "<link\\s+[^>]*href=\\\"~/css/([^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ThemeOption = new(
        "data-theme-option=\\\"([^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HardCodedColor = new(
        "(?i)#[0-9a-f]{3,8}\\b|\\brgba?\\s*\\(|\\bhsla?\\s*\\(",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Layout_LoadsOnlyCanonicalActiveStylesheets()
    {
        var layout = ReadRepositoryFile("src/CaddyUi.Web/Pages/Shared/_Layout.cshtml");
        var stylesheets = StylesheetReference
            .Matches(layout)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(
            stylesheets.SetEquals(
                new[]
                {
                    "site.css",
                    "features.css",
                    "operations.css",
                }),
            "The layout must load only site.css, features.css, and operations.css.");
    }

    [Fact]
    public void RetiredPhaseStylesheets_AreAbsent()
    {
        Assert.False(File.Exists(RepositoryPath("src/CaddyUi.Web/wwwroot/css/phase3.css")));
        Assert.False(File.Exists(RepositoryPath("src/CaddyUi.Web/wwwroot/css/phase6.css")));
    }

    [Fact]
    public void Layout_OffersExactlySystemLightAndDarkThemeOptions()
    {
        var layout = ReadRepositoryFile("src/CaddyUi.Web/Pages/Shared/_Layout.cshtml");
        var options = ThemeOption
            .Matches(layout)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(
            options.SetEquals(
                new[]
                {
                    "system",
                    "light",
                    "dark",
                }),
            "The theme selector must expose exactly system, light, and dark.");
    }

    [Fact]
    public void ThemeAndDialogEnhancements_AreSharedAcrossTheServerRenderedUi()
    {
        var layout = ReadRepositoryFile("src/CaddyUi.Web/Pages/Shared/_Layout.cshtml");
        var login = ReadRepositoryFile("src/CaddyUi.Web/Pages/Login.cshtml");
        var portalLogin = ReadRepositoryFile("src/CaddyUi.Web/Pages/Portal/Login.cshtml");

        Assert.Contains("~/js/theme-init.js", layout, StringComparison.Ordinal);
        Assert.Contains("~/js/theme-init.js", login, StringComparison.Ordinal);
        Assert.Contains(
            "/__caddy_ui_auth/assets/portal.css",
            portalLogin,
            StringComparison.Ordinal);
        Assert.DoesNotContain("~/js/theme-init.js", portalLogin, StringComparison.Ordinal);
        Assert.Contains("~/js/dialogs.js", layout, StringComparison.Ordinal);
        Assert.Contains("data-confirm-dialog", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductUi_DefaultsToGerman()
    {
        var settings = ReadRepositoryFile("src/CaddyUi.Web/appsettings.json");

        Assert.Contains("\"DefaultCulture\": \"de\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void SiteStyles_ProvideCentralAliasesAndAccessibilityThemeRules()
    {
        var source = ReadRepositoryFile("src/CaddyUi.Web/wwwroot/css/site.css");

        foreach (var requiredAlias in new[]
                 {
                     "--ui-bg:",
                     "--ui-surface:",
                     "--ui-accent:",
                     "--ui-focus:",
                     "--ui-space-1:",
                     "--ui-radius-control:",
                 })
        {
            Assert.Contains(requiredAlias, source, StringComparison.Ordinal);
        }

        Assert.Contains("@media (prefers-color-scheme: dark)", source, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", source, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/CaddyUi.Web/wwwroot/css/features.css")]
    [InlineData("src/CaddyUi.Web/wwwroot/css/operations.css")]
    public void FeatureStyles_UseSemanticTokensInsteadOfHardCodedColors(string relativePath)
    {
        var source = ReadRepositoryFile(relativePath);

        Assert.DoesNotMatch(HardCodedColor, source);
    }

    [Fact]
    public void AgentGuide_MakesMicrosoftFluent2WebMandatory()
    {
        var guide = ReadRepositoryFile("AGENTS.md");

        Assert.Contains(
            "Microsoft Fluent 2 Web is the mandatory application-wide design system.",
            guide,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(RepositoryPath(relativePath));
    }

    private static string RepositoryPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CaddyUi.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
