namespace CaddyUi.Web.Tests;

public sealed class DomainFirstRoutingMarkupTests
{
    [Fact]
    public void RouteOverview_GroupsRoutesByDomainAndUsesQuickCreateDialog()
    {
        var markup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Routing/Index.cshtml"));

        Assert.Contains("data-domain-route-group", markup, StringComparison.Ordinal);
        Assert.Contains("data-dialog-open=\"@dialogId\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"QuickCreate\"", markup, StringComparison.Ordinal);
        Assert.Contains("name=\"QuickRoute.Name\"", markup, StringComparison.Ordinal);
        Assert.Contains("name=\"QuickRoute.Upstream\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RouteOverview_ExposesPublicRouteAndKeepsAdvancedCreationAvailable()
    {
        var markup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Routing/Index.cshtml"));

        Assert.Contains("IndexModel.PublicUrl(definition)", markup, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", markup, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-route-domainId=\"@group.Domain.Id\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCreate_OffersExplicitSaveOnlyAndSaveApplyActions()
    {
        var markup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Routing/Index.cshtml"));
        var pageModel = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Routing/Index.cshtml.cs"));

        Assert.Contains("name=\"applyAfterSave\"", markup, StringComparison.Ordinal);
        Assert.Contains("value=\"false\">Speichern</button>", markup, StringComparison.Ordinal);
        Assert.Contains("value=\"true\">Erstellen &amp; aktivieren</button>", markup, StringComparison.Ordinal);
        Assert.Contains("bool? applyAfterSave", pageModel, StringComparison.Ordinal);
        Assert.Contains("var shouldApply = applyAfterSave ?? true;", pageModel, StringComparison.Ordinal);

        var saveOnlyBranch = pageModel.IndexOf("if (!shouldApply)", StringComparison.Ordinal);
        var previewCall = pageModel.IndexOf("CreatePreviewAsync", StringComparison.Ordinal);
        Assert.True(saveOnlyBranch >= 0);
        Assert.True(previewCall > saveOnlyBranch);
        Assert.Contains(
            "Route created. The active Caddy configuration is unchanged.",
            pageModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCreate_UsesWildcardAndSafeApplyPipeline()
    {
        var pageModel = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Routing/Index.cshtml.cs"));

        Assert.Contains("RouteCertificateMode.Wildcard", pageModel, StringComparison.Ordinal);
        Assert.Contains("CreatePreviewAsync", pageModel, StringComparison.Ordinal);
        Assert.Contains("ApplyAsync", pageModel, StringComparison.Ordinal);
        Assert.Contains("definition with { Enabled = false }", pageModel, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteOverview_DistinguishesDesiredStateFromActiveCaddyState()
    {
        var markup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Routing/Index.cshtml"));
        var pageModel = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Routing/Index.cshtml.cs"));

        Assert.Contains("Model.RuntimeState.ActiveStateTracked", markup, StringComparison.Ordinal);
        Assert.Contains("IndexModel.StateLabel(runtimeState)", markup, StringComparison.Ordinal);
        Assert.Contains("Model.RuntimeState.PendingRemovals", markup, StringComparison.Ordinal);
        Assert.Contains("Aktivstatus unbekannt", markup, StringComparison.Ordinal);
        Assert.Contains("Entfernung offen", markup, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "@(definition.Enabled ? \"Aktiv\" : \"Deaktiviert\")",
            markup,
            StringComparison.Ordinal);
        Assert.Contains("RouteRuntimeState.PendingRemoval", pageModel, StringComparison.Ordinal);
        Assert.Contains("remains active in Caddy until", pageModel, StringComparison.Ordinal);
    }

    [Fact]
    public void DomainRouteLayout_HasMobileSingleColumnBehavior()
    {
        var css = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/wwwroot/css/features.css"));

        Assert.Contains(".domain-route-row", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 760px)", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", css, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            $"Repository file '{relativePath}' could not be located.");
    }
}
