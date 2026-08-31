namespace CaddyUi.Web.Tests;

public sealed class TaskFocusedNavigationMarkupTests
{
    [Fact]
    public void Sidebar_UsesTaskFocusedTopLevelNavigation()
    {
        var markup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Shared/_Layout.cshtml"));

        Assert.Contains("Domains &amp; DNS", markup, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Routing/Index\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Traffic/Index\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Requests/Index\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/SystemStatus/Index\"", markup, StringComparison.Ordinal);

        Assert.DoesNotContain("asp-page=\"/Operations/Jobs\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page=\"/Operations/Backup\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page=\"/Operations/Cutover\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page=\"/LiveLog/Index\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page=\"/Performance/Index\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenOperationalPages_AreReachableFromTheirParentWorkspaces()
    {
        var system = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/SystemStatus/Index.cshtml"));
        var traffic = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Traffic/Index.cshtml"));
        var requests = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Requests/Index.cshtml"));

        Assert.Contains("asp-page=\"/Operations/Jobs\"", system, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Operations/Backup\"", system, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Operations/Cutover\"", system, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Performance/Index\"", traffic, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Routes/Analytics\"", traffic, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/LiveLog/Index\"", requests, StringComparison.Ordinal);
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
