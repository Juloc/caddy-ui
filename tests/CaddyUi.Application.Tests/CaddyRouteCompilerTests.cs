using System.Text.Json;
using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Tests;

[Collection(CaddyCertificateSourceRegistryCollection.Name)]
public sealed class CaddyRouteCompilerTests : IDisposable
{
    public CaddyRouteCompilerTests()
    {
        CaddyCertificateSourceRegistry.Clear();
    }

    public void Dispose()
    {
        CaddyCertificateSourceRegistry.Clear();
    }

    [Fact]
    public void Compile_GeneratesDeterministicProtectedProxyRoute()
    {
        var accessGroupId = Guid.NewGuid();
        var route = CreateProxy(
            "mealie.example.com",
            "/",
            RouteCertificateMode.Individual,
            accessGroupId);
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        var first = compiler.Compile([new CaddyRouteSource(route, "Family")]);
        var second = compiler.Compile([new CaddyRouteSource(route, "Family")]);

        Assert.Equal(first.Content, second.Content);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Contains("mealie.example.com {", first.Content, StringComparison.Ordinal);
        Assert.Contains("output file /var/log/caddy/access.log", first.Content, StringComparison.Ordinal);
        Assert.Contains("forward_auth 127.0.0.1:8099", first.Content, StringComparison.Ordinal);
        Assert.Contains($"group={accessGroupId:D}", first.Content, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy \"mealie:9925\"", first.Content, StringComparison.Ordinal);
        Assert.False(first.RequiresWildcardCertificateRenderer);
        Assert.True(first.CertificateReadyForActiveApply);
    }

    [Fact]
    public void Compile_MarksUnresolvedInheritedCertificateAsBlocked()
    {
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");
        var compilation = compiler.Compile([
            new CaddyRouteSource(
                CreateProxy("mealie.example.com", "/", RouteCertificateMode.Inherit, null),
                string.Empty),
        ]);

        Assert.True(compilation.RequiresWildcardCertificateRenderer);
        Assert.False(compilation.CertificateReadyForActiveApply);
        Assert.NotEmpty(compilation.Warnings);
        using var manifest = JsonDocument.Parse(compilation.ManifestJson);
        Assert.True(manifest.RootElement.GetProperty("usesWildcardCertificates").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("requiresWildcardCertificateRenderer").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("certificateReadyForActiveApply").GetBoolean());
    }

    [Fact]
    public void Compile_RendersNetcupDnsChallengeFromEnvironmentSecretReferences()
    {
        var route = CreateProxy(
            "mealie.example.com",
            "/",
            RouteCertificateMode.Inherit,
            null);
        var provider = NetcupProvider();
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        var compilation = compiler.Compile([
            new CaddyRouteSource(route, string.Empty, "wildcard", provider),
        ]);

        Assert.False(compilation.RequiresWildcardCertificateRenderer);
        Assert.True(compilation.CertificateReadyForActiveApply);
        Assert.Contains("tls {", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("dns netcup {", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("customer_number \"123456\"", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("api_key \"{env.NETCUP_API_KEY}\"", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("api_password \"{env.NETCUP_API_PASSWORD}\"", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("*.example.com {", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("host \"mealie.example.com\"", compilation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RendersRequestedWildcardAndBaseCertificateWithoutRoutes()
    {
        var domainId = Guid.NewGuid();
        CaddyCertificateSourceRegistry.Replace([
            new CaddyDomainCertificateSource(
                domainId,
                "example.com",
                "wildcard",
                true,
                true,
                NetcupProvider()),
        ]);
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        var compilation = compiler.Compile(Array.Empty<CaddyRouteSource>());

        Assert.True(compilation.CertificateReadyForActiveApply);
        Assert.Contains("*.example.com, example.com {", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("respond \"No managed route matched\" 404", compilation.Content, StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(compilation.ManifestJson);
        var names = manifest.RootElement.GetProperty("certificates")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("*.example.com", names);
        Assert.Contains("example.com", names);
    }

    [Fact]
    public void Compile_RendersBaseOnlyCertificateWithoutDnsProvider()
    {
        CaddyCertificateSourceRegistry.Replace([
            new CaddyDomainCertificateSource(
                Guid.NewGuid(),
                "example.com",
                "individual",
                false,
                true,
                null),
        ]);
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        var compilation = compiler.Compile(Array.Empty<CaddyRouteSource>());

        Assert.True(compilation.CertificateReadyForActiveApply);
        Assert.False(compilation.RequiresWildcardCertificateRenderer);
        Assert.Contains("example.com {", compilation.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("dns netcup", compilation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MarksDeepWildcardRouteAsNotGenerated()
    {
        var route = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Deep",
            Guid.NewGuid(),
            "example.com",
            "api.internal",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Wildcard,
            null,
            RouteConfigurationDocument.Empty with { Upstream = "api:8080" });
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        var compilation = compiler.Compile([
            new CaddyRouteSource(route, string.Empty, "wildcard", NetcupProvider()),
        ]);

        Assert.False(compilation.CertificateReadyForActiveApply);
        Assert.Contains(compilation.Warnings, warning =>
            warning.Contains("nicht abgedeckt", StringComparison.Ordinal));
        using var manifest = JsonDocument.Parse(compilation.ManifestJson);
        Assert.False(manifest.RootElement.GetProperty("routes")[0].GetProperty("generated").GetBoolean());
    }

    [Fact]
    public void Compile_BlocksWildcardWhenConfiguredModuleIsNotInstalled()
    {
        var provider = new CaddyDnsProviderSource(
            "cloudflare",
            true,
            false,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["api_token"] = "CLOUDFLARE_API_TOKEN" });
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        var compilation = compiler.Compile([
            new CaddyRouteSource(
                CreateProxy("app.example.com", "/", RouteCertificateMode.Wildcard, null),
                string.Empty,
                "wildcard",
                provider),
        ]);

        Assert.True(compilation.RequiresWildcardCertificateRenderer);
        Assert.False(compilation.CertificateReadyForActiveApply);
        Assert.Contains(compilation.Warnings, warning => warning.Contains("nicht als installiert", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_RejectsDuplicateHostAndPath()
    {
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");
        var first = CreateProxy("example.com", "/api", RouteCertificateMode.Individual, null);
        var second = CreateProxy("example.com", "/api", RouteCertificateMode.Individual, null) with
        {
            Id = Guid.NewGuid(),
            Name = "Second",
        };

        Assert.Throws<InvalidOperationException>(() => compiler.Compile([
            new CaddyRouteSource(first, string.Empty),
            new CaddyRouteSource(second, string.Empty),
        ]));
    }

    [Fact]
    public void Compile_RejectsCustomRouteWhenFeatureIsDisabled()
    {
        var custom = ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Custom",
            Guid.NewGuid(),
            "example.com",
            "custom",
            ManagedRouteKind.Custom,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with { CustomSnippet = "respond \"ok\" 200" });
        var compiler = new CaddyRouteCompiler(false, "127.0.0.1:8099");

        Assert.Throws<InvalidOperationException>(() => compiler.Compile([
            new CaddyRouteSource(custom, string.Empty),
        ]));
    }

    [Fact]
    public void LineDiff_SeparatesAddedAndRemovedLines()
    {
        var diff = LineDiff.Create("a\nb", "a\nc");

        Assert.Contains(diff, line => line.Kind == DiffLineKind.Removed && line.Text == "b");
        Assert.Contains(diff, line => line.Kind == DiffLineKind.Added && line.Text == "c");
    }

    private static CaddyDnsProviderSource NetcupProvider()
    {
        return new CaddyDnsProviderSource(
            "netcup",
            true,
            true,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["customer_number"] = "123456",
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api_key"] = "secret://env/NETCUP_API_KEY",
                ["api_password"] = "NETCUP_API_PASSWORD",
            });
    }

    private static ManagedRouteDefinition CreateProxy(
        string host,
        string path,
        RouteCertificateMode certificateMode,
        Guid? accessGroupId)
    {
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        var subdomain = dot < 0 ? string.Empty : host[..dot];
        var domain = dot < 0 ? host : host[(dot + 1)..];
        return ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Mealie",
            Guid.NewGuid(),
            domain,
            subdomain,
            ManagedRouteKind.Proxy,
            true,
            0,
            certificateMode,
            accessGroupId,
            RouteConfigurationDocument.Empty with
            {
                PathPrefix = path,
                Upstream = "mealie:9925",
            });
    }
}
