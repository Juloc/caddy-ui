namespace CaddyUi.Web.Tests;

public sealed class PortalRouteMarkupTests
{
    [Fact]
    public void PortalPages_UseTheInternalPathsGeneratedByCaddy()
    {
        var authorizeMarkup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Portal/Authorize.cshtml"));
        var loginMarkup = File.ReadAllText(FindRepositoryFile(
            "src/CaddyUi.Web/Pages/Portal/Login.cshtml"));

        Assert.Contains(
            "@page \"/__caddy_ui_auth/authorize\"",
            authorizeMarkup,
            StringComparison.Ordinal);
        Assert.Contains(
            "@page \"/__caddy_ui_auth/login\"",
            loginMarkup,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "@page \"/portal/authorize\"",
            authorizeMarkup,
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
