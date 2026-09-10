namespace CaddyUi.Web.Tests;

public sealed class UiUxContractTests
{
    [Fact]
    public void RoutingOverview_UsesCanonicalDomainFirstSemanticList()
    {
        var markup = ReadRepositoryFile("src/CaddyUi.Web/Pages/Routing/Index.cshtml");

        Assert.Contains("data-domain-route-group", markup, StringComparison.Ordinal);
        Assert.Contains("<ul class=\"domain-route-list\">", markup, StringComparison.Ordinal);
        Assert.Contains("class=\"domain-route-row", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResponsiveContract_ContainsCanonicalShellDialogAndTouchBreakpoints()
    {
        var siteCss = ReadRepositoryFile("src/CaddyUi.Web/wwwroot/css/site.css");
        var featureCss = ReadRepositoryFile("src/CaddyUi.Web/wwwroot/css/features.css");

        Assert.Contains("@media (max-width: 1024px)", siteCss, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 760px)", siteCss, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 639px)", siteCss, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 420px)", siteCss, StringComparison.Ordinal);
        Assert.Contains("@media (pointer: coarse)", siteCss, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", siteCss, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1180px)", featureCss, StringComparison.Ordinal);
        Assert.Contains(".domain-route-row", featureCss, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedDialogAndMobileNavigation_PreserveKeyboardFocusContract()
    {
        var dialogs = ReadRepositoryFile("src/CaddyUi.Web/wwwroot/js/dialogs.js");
        var shell = ReadRepositoryFile("src/CaddyUi.Web/wwwroot/js/shell.js");

        Assert.Contains("dialog.showModal()", dialogs, StringComparison.Ordinal);
        Assert.Contains("opener.focus()", dialogs, StringComparison.Ordinal);
        Assert.Contains("pendingForm.requestSubmit(pendingSubmitter ?? undefined)", dialogs, StringComparison.Ordinal);

        Assert.Contains("element.inert = isInert", shell, StringComparison.Ordinal);
        Assert.Contains("sidebar.inert = !isOpen", shell, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Escape\"", shell, StringComparison.Ordinal);
        Assert.Contains("trapDrawerFocus(event)", shell, StringComparison.Ordinal);
        Assert.Contains("previouslyFocusedElement.focus()", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedProviderAndDnsRowOperations_AreSecondaryActions()
    {
        var providers = ReadRepositoryFile("src/CaddyUi.Web/Pages/Administration/Providers.cshtml");
        var dns = ReadRepositoryFile("src/CaddyUi.Web/Pages/Operations/Dns.cshtml");

        Assert.Contains(
            "<button class=\"button button--compact\" type=\"submit\" disabled=\"@(!provider.Enabled)\">@T[\"Test\"]</button>",
            providers,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<button class=\"button button--compact button--primary\" type=\"submit\" disabled=\"@(!provider.Enabled)\">@T[\"Test\"]</button>",
            providers,
            StringComparison.Ordinal);

        Assert.Contains(
            "<button class=\"button button--compact\" type=\"submit\" disabled=\"@(!record.Enabled)\">@T[\"Synchronize\"]</button>",
            dns,
            StringComparison.Ordinal);
        Assert.Contains(
            "<button class=\"button button--compact\" type=\"submit\" disabled=\"@(!target.Enabled)\">@T[\"Run now\"]</button>",
            dns,
            StringComparison.Ordinal);

        Assert.Contains(
            "<button class=\"button button--primary\" type=\"submit\">@T[\"Create record\"]</button>",
            dns,
            StringComparison.Ordinal);
        Assert.Contains(
            "<button class=\"button button--primary\" type=\"submit\">@T[\"Create DDNS target\"]</button>",
            dns,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeAndAccessibilityContract_ProvidesSystemDarkReducedMotionAndContrastModes()
    {
        var css = ReadRepositoryFile("src/CaddyUi.Web/wwwroot/css/site.css");

        Assert.Contains("html[data-theme=\"dark\"]", css, StringComparison.Ordinal);
        Assert.Contains("html[data-theme=\"system\"]", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme: dark)", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-contrast: more)", css, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", css, StringComparison.Ordinal);
        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
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
