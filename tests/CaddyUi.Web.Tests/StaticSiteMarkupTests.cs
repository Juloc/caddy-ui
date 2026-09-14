namespace CaddyUi.Web.Tests;

public sealed class StaticSiteMarkupTests
{
    [Fact]
    public void SidebarAndEditor_ExposeTaskFocusedStaticSiteWorkflow()
    {
        var layout = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Shared/_Layout.cshtml"));
        var editor = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/StaticSites/Edit.cshtml"));
        var overview = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/StaticSites/Index.cshtml"));

        Assert.Contains("asp-page=\"/StaticSites/Index\"", layout, StringComparison.Ordinal);
        Assert.Contains("asp-antiforgery=\"true\"", editor, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"SaveApply\"", editor, StringComparison.Ordinal);
        Assert.Contains("data-oauth-template", editor, StringComparison.Ordinal);
        Assert.Contains("/privacy", editor, StringComparison.Ordinal);
        Assert.Contains("/terms", editor, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"Delete\"", overview, StringComparison.Ordinal);
        Assert.Contains("asp-antiforgery=\"true\"", overview, StringComparison.Ordinal);
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
