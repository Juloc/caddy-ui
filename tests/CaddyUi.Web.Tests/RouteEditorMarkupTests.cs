namespace CaddyUi.Web.Tests;

public sealed class RouteEditorMarkupTests
{
    [Fact]
    public void Editor_KeepsSpecializedRoutingFieldsBehindAdvancedControls()
    {
        var markup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Routing/Edit.cshtml"));

        var routingToggle = markup.IndexOf(
            "data-disclosure-toggle=\"routing-advanced\"",
            StringComparison.Ordinal);
        var pathPrefix = markup.IndexOf(
            "asp-for=\"Input.PathPrefix\"",
            StringComparison.Ordinal);
        var sortOrder = markup.IndexOf(
            "asp-for=\"Input.SortOrder\"",
            StringComparison.Ordinal);

        Assert.True(routingToggle >= 0);
        Assert.True(pathPrefix > routingToggle);
        Assert.True(sortOrder > routingToggle);
        Assert.Contains(
            "data-disclosure-toggle=\"proxy-advanced\"",
            markup,
            StringComparison.Ordinal);
        Assert.Contains(
            "data-health-check-toggle",
            markup,
            StringComparison.Ordinal);
        Assert.Contains(
            "placeholder=\"/health/ready\"",
            markup,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "placeholder=\"/health\"",
            markup,
            StringComparison.Ordinal);
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
