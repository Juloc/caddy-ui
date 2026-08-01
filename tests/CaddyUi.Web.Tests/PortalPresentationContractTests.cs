namespace CaddyUi.Web.Tests;

public sealed class PortalPresentationContractTests
{
    [Fact]
    public void PortalUsesDedicatedFluentSurfaceAndReservedAssetPath()
    {
        var markup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Portal/Login.cshtml"));
        var styles = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/wwwroot/portal/portal.css"));
        var program = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Program.cs"));

        Assert.Contains("portal-card", markup, StringComparison.Ordinal);
        Assert.Contains("--portal-accent", markup, StringComparison.Ordinal);
        Assert.Contains("portal-button", markup, StringComparison.Ordinal);
        Assert.Contains(
            "/__caddy_ui_auth/assets/portal.css",
            markup,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequestPath = \"/__caddy_ui_auth/assets\"",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Segoe UI Variable Text\"",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            "@media (prefers-color-scheme: dark)",
            styles,
            StringComparison.Ordinal);
        Assert.Contains(
            "@media (prefers-reduced-motion: reduce)",
            styles,
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
