using CaddyUi.Web.Pages.Routing;

namespace CaddyUi.Web.Tests;

public sealed class RouteEditorSaveTests
{
    [Fact]
    public void RedirectRoute_NormalizesInactiveLegacyValuesBeforeSave()
    {
        var input = new EditModel.RouteInput
        {
            Kind = "redirect",
            Upstream = "legacy:8080",
            PreserveHost = true,
            HealthPath = "/health",
            HealthIntervalSeconds = 0,
            RedirectTarget = "https://example.com",
            StaticStatusCode = 0,
            StaticBody = "legacy",
            CustomSnippet = "respond legacy",
        };

        input.NormalizeInactiveConfiguration();

        Assert.Empty(input.Upstream);
        Assert.False(input.PreserveHost);
        Assert.Empty(input.HealthPath);
        Assert.Equal(30, input.HealthIntervalSeconds);
        Assert.Equal("https://example.com", input.RedirectTarget);
        Assert.Equal(200, input.StaticStatusCode);
        Assert.Empty(input.StaticBody);
        Assert.Empty(input.CustomSnippet);
    }

    [Fact]
    public void RouteEditor_UsesExplicitPostProtectionAndVisibleValidation()
    {
        var source = ReadRepositoryFile("src/CaddyUi.Web/Pages/Routing/Edit.cshtml");

        Assert.Contains("asp-antiforgery=\"true\"", source, StringComparison.Ordinal);
        Assert.Contains("asp-validation-summary=\"All\"", source, StringComparison.Ordinal);
        Assert.Contains("control.disabled = !active;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteEditor_RemovesInactiveValidationErrorsBeforeSaving()
    {
        var source = ReadRepositoryFile("src/CaddyUi.Web/Pages/Routing/Edit.cshtml.cs");

        Assert.Contains("RemoveInactiveFieldValidationErrors();", source, StringComparison.Ordinal);
        Assert.Contains("Input.NormalizeInactiveConfiguration();", source, StringComparison.Ordinal);
        Assert.Contains("Route save or activation failed", source, StringComparison.Ordinal);
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
