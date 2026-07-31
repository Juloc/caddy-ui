using System.Reflection;
using System.Xml.Linq;
using CaddyUi.Web.Pages.Routing;

namespace CaddyUi.Web.Tests;

public sealed class MultilingualDnsRouteFeatureContractTests
{
    [Fact]
    public void RouteEditor_ProvidesSeparateSaveAndSaveApplyHandlers()
    {
        var methods = typeof(EditModel)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("OnPostSaveAsync", methods);
        Assert.Contains("OnPostSaveApplyAsync", methods);
    }

    [Fact]
    public void RouteEditor_RendersBothExplicitActions()
    {
        var source = ReadRepositoryFile("src/CaddyUi.Web/Pages/Routing/Edit.cshtml");

        Assert.Contains("asp-page-handler=\"Save\"", source, StringComparison.Ordinal);
        Assert.Contains("asp-page-handler=\"SaveApply\"", source, StringComparison.Ordinal);
        Assert.Contains("T[\"Save\"]", source, StringComparison.Ordinal);
        Assert.Contains("T[isEdit ? \"Save and update\" : \"Save and activate\"]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDnsBrowser_IsReadOnlyAndUsesProviderReaders()
    {
        var page = ReadRepositoryFile("src/CaddyUi.Web/Pages/Administration/ProviderDns.cshtml");
        var service = ReadRepositoryFile("src/CaddyUi.Infrastructure/Operations/DnsProviderRecordQueryService.cs");

        Assert.Contains("Read the current DNS records directly from the selected provider", page, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"post\"", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NetcupDnsProviderRecordReader", service, StringComparison.Ordinal);
        Assert.Contains("CommonRestDnsProviderRecordReader", service, StringComparison.Ordinal);
        Assert.Contains("infoDnsRecords", service, StringComparison.Ordinal);
    }

    [Fact]
    public void GermanResource_HasUniqueKeys()
    {
        var resource = XDocument.Parse(
            ReadRepositoryFile("src/CaddyUi.Web/Resources/SharedResource.de.resx"));
        var keys = resource.Root!
            .Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
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
