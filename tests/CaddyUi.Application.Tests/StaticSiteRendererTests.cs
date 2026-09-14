using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Tests;

[Collection(CaddyCertificateSourceRegistryCollection.Name)]
public sealed class StaticSiteRendererTests : IDisposable
{
    public StaticSiteRendererTests()
    {
        CaddyCertificateSourceRegistry.Clear();
    }

    public void Dispose()
    {
        CaddyCertificateSourceRegistry.Clear();
    }

    [Fact]
    public void Render_EncodesManagedContentAndExposesLegalPaths()
    {
        var configuration = RouteConfigurationDocument.Empty with
        {
            StaticSiteTitle = "Paperless <Backup>",
            StaticSiteContact = "owner@example.com",
            StaticSiteHomeText = "Private backup application.",
            StaticSitePrivacyText = "No data is sold.\n\n<script>alert('x')</script>",
            StaticSiteTermsText = "Private use only.",
        };

        var html = StaticSiteRenderer.Render(configuration);

        Assert.Contains("Paperless &lt;Backup&gt;", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/privacy\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/terms\"", html, StringComparison.Ordinal);
        Assert.Contains("owner@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert('x')</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RendersStaticSiteAsHtmlWithoutCustomRouteFeature()
    {
        var domainId = Guid.NewGuid();
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Mini-site",
            domainId,
            "example.com",
            "paperless-backup",
            ManagedRouteKind.StaticSite,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with
            {
                StaticSiteTitle = "Paperless Backup",
                StaticSiteHomeText = "Private backup application.",
                StaticSitePrivacyText = "No data is sold.",
                StaticSiteTermsText = "Private use only.",
            });
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        var compilation = compiler.Compile([new CaddyRouteSource(route, string.Empty)]);

        Assert.Contains("paperless-backup.example.com {", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("Content-Type \"text/html; charset=utf-8\"", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("# Mini-site · static_site", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("href=\\\"/privacy\\\"", compilation.Content, StringComparison.Ordinal);
        Assert.True(compilation.CertificateReadyForActiveApply);
    }
}
