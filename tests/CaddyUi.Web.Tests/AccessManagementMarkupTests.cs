namespace CaddyUi.Web.Tests;

public sealed class AccessManagementMarkupTests
{
    [Fact]
    public void AccessPage_OffersCompleteGroupAndCredentialActions()
    {
        var markup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Access/Index.cshtml"));

        Assert.Contains("asp-page-handler=\"UpdateGroup\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"DeleteGroup\"", markup, StringComparison.Ordinal);
        Assert.Contains("EnableGroup", markup, StringComparison.Ordinal);
        Assert.Contains("DisableGroup", markup, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"UpdateCredential\"", markup, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"DeleteCredential\"", markup, StringComparison.Ordinal);
        Assert.Contains("EnableCredential", markup, StringComparison.Ordinal);
        Assert.Contains("DisableCredential", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleGroup", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleCredential", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"enabled\"", markup, StringComparison.Ordinal);
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
