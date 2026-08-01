using CaddyUi.Web.Pages.Access;

namespace CaddyUi.Web.Tests;

public sealed class AccessStateActionContractTests
{
    [Theory]
    [InlineData(nameof(IndexModel.OnPostEnableGroupAsync))]
    [InlineData(nameof(IndexModel.OnPostDisableGroupAsync))]
    [InlineData(nameof(IndexModel.OnPostEnableCredentialAsync))]
    [InlineData(nameof(IndexModel.OnPostDisableCredentialAsync))]
    public void PageModel_ExposesExplicitStateHandler(string methodName)
    {
        var method = typeof(IndexModel).GetMethod(methodName, [typeof(Guid)]);

        Assert.NotNull(method);
    }

    [Fact]
    public void Markup_DoesNotPostAnAmbiguousBooleanToggle()
    {
        var repositoryRoot = FindRepositoryRoot();
        var markup = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "src",
                "CaddyUi.Web",
                "Pages",
                "Access",
                "Index.cshtml"));

        Assert.Contains("EnableGroup", markup, StringComparison.Ordinal);
        Assert.Contains("DisableGroup", markup, StringComparison.Ordinal);
        Assert.Contains("EnableCredential", markup, StringComparison.Ordinal);
        Assert.Contains("DisableCredential", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"enabled\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleGroup", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleCredential", markup, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CaddyUi.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root could not be located.");
    }
}
