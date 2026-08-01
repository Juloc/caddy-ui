#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path: str, content: str) -> None:
    (ROOT / path).write_text(content, encoding="utf-8", newline="\n")


def replace(path: str, old: str, new: str) -> None:
    content = read(path)
    if old not in content:
        raise RuntimeError(f"Expected text not found in {path}: {old[:120]!r}")
    write(path, content.replace(old, new))


def add_resx_entries(path: str, entries: dict[str, str]) -> None:
    content = read(path)
    blocks: list[str] = []
    for key, value in entries.items():
        if f'name="{key}"' in content:
            continue
        blocks.append(
            f'  <data name="{escape(key)}" xml:space="preserve">\n'
            f'    <value>{escape(value)}</value>\n'
            f'  </data>'
        )
    if not blocks:
        return
    if "</root>" not in content:
        raise RuntimeError(f"Invalid RESX file: {path}")
    write(path, content.replace("</root>", "\n".join(blocks) + "\n</root>"))


replace(
    "src/CaddyUi.Domain/Routing/RouteModels.cs",
    """    string StaticBody,
    string CustomSnippet)
""",
    """    string StaticBody,
    string CustomSnippet,
    bool SkipUpstreamTlsVerification = false)
""",
)
replace(
    "src/CaddyUi.Domain/Routing/RouteModels.cs",
    """        true,
        200,
        string.Empty,
        string.Empty);
""",
    """        true,
        200,
        string.Empty,
        string.Empty,
        false);
""",
)
replace(
    "src/CaddyUi.Domain/Routing/RouteModels.cs",
    """        return configuration with
        {
""",
    """        if (configuration.SkipUpstreamTlsVerification &&
            (kind != ManagedRouteKind.Proxy ||
             !upstream.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Skipping upstream TLS verification requires an HTTPS proxy upstream.",
                nameof(configuration));
        }

        return configuration with
        {
""",
)
replace(
    "src/CaddyUi.Domain/Routing/RouteModels.cs",
    """            StaticBody = staticBody,
            CustomSnippet = customSnippet,
        };
""",
    """            StaticBody = staticBody,
            CustomSnippet = customSnippet,
            SkipUpstreamTlsVerification =
                kind == ManagedRouteKind.Proxy && configuration.SkipUpstreamTlsVerification,
        };
""",
)

replace(
    "src/CaddyUi.Application/Routing/CaddyRouteCompiler.cs",
    """        if (!configuration.PreserveHost && configuration.HealthPath.Length == 0)
""",
    """        if (!configuration.PreserveHost &&
            configuration.HealthPath.Length == 0 &&
            !configuration.SkipUpstreamTlsVerification)
""",
)
replace(
    "src/CaddyUi.Application/Routing/CaddyRouteCompiler.cs",
    """        if (configuration.HealthPath.Length > 0)
        {
""",
    """        if (configuration.SkipUpstreamTlsVerification)
        {
            builder.AppendLine("            transport http {");
            builder.AppendLine("                tls_insecure_skip_verify");
            builder.AppendLine("            }");
        }

        if (configuration.HealthPath.Length > 0)
        {
""",
)

replace(
    "src/CaddyUi.Web/Pages/Routing/Edit.cshtml.cs",
    """                Input.StaticStatusCode,
                Input.StaticBody,
                Input.CustomSnippet);
""",
    """                Input.StaticStatusCode,
                Input.StaticBody,
                Input.CustomSnippet,
                Input.SkipUpstreamTlsVerification);
""",
)
replace(
    "src/CaddyUi.Web/Pages/Routing/Edit.cshtml.cs",
    """        public bool PreserveHost { get; set; }

        [DisplayFormat(ConvertEmptyStringToNull = false)]
""",
    """        public bool PreserveHost { get; set; }

        public bool SkipUpstreamTlsVerification { get; set; }

        [DisplayFormat(ConvertEmptyStringToNull = false)]
""",
)
replace(
    "src/CaddyUi.Web/Pages/Routing/Edit.cshtml.cs",
    """                    PreserveHost = false;
                    HealthPath = string.Empty;
""",
    """                    PreserveHost = false;
                    SkipUpstreamTlsVerification = false;
                    HealthPath = string.Empty;
""",
)
replace(
    "src/CaddyUi.Web/Pages/Routing/Edit.cshtml.cs",
    """                PreserveHost = route.Configuration.PreserveHost,
                HealthPath = route.Configuration.HealthPath,
""",
    """                PreserveHost = route.Configuration.PreserveHost,
                SkipUpstreamTlsVerification = route.Configuration.SkipUpstreamTlsVerification,
                HealthPath = route.Configuration.HealthPath,
""",
)

replace(
    "src/CaddyUi.Web/Pages/Routing/Edit.cshtml",
    """            <label class="check field--wide">
                <input asp-for="Input.PreserveHost" />
                <span><strong>@T["Preserve original Host header"]</strong><small>@T["Enable only when the upstream expects the public host."]</small></span>
            </label>
""",
    """            <label class="check field--wide">
                <input asp-for="Input.PreserveHost" />
                <span><strong>@T["Preserve original Host header"]</strong><small>@T["Enable only when the upstream expects the public host."]</small></span>
            </label>
            <label class="check field--wide">
                <input asp-for="Input.SkipUpstreamTlsVerification" />
                <span><strong>@T["Allow self-signed upstream certificate"]</strong><small>@T["Disables certificate verification only for a trusted internal HTTPS service such as Proxmox."]</small></span>
            </label>
""",
)

add_resx_entries(
    "src/CaddyUi.Web/Resources/SharedResource.de.resx",
    {
        "Allow self-signed upstream certificate": "Selbstsigniertes Upstream-Zertifikat erlauben",
        "Disables certificate verification only for a trusted internal HTTPS service such as Proxmox.": "Deaktiviert die Zertifikatsprüfung nur für einen vertrauenswürdigen internen HTTPS-Dienst wie Proxmox.",
    },
)

write(
    "tests/CaddyUi.Application.Tests/UpstreamTlsCompatibilityTests.cs",
    """using System.Text.Json;
using CaddyUi.Application.Routing;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Tests;

public sealed class UpstreamTlsCompatibilityTests
{
    [Fact]
    public void Compile_RendersExplicitSelfSignedHttpsTransport()
    {
        var route = CreateProxy(
            "https://192.168.1.10:8006",
            skipUpstreamTlsVerification: true);
        var compiler = new CaddyRouteCompiler(false, "caddy-ui:8099");

        var compilation = compiler.Compile([new CaddyRouteSource(route, string.Empty)]);

        Assert.Contains("reverse_proxy \\\"https://192.168.1.10:8006\\\"", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("transport http {", compilation.Content, StringComparison.Ordinal);
        Assert.Contains("tls_insecure_skip_verify", compilation.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsTlsBypassForPlainHttpUpstream()
    {
        Assert.Throws<ArgumentException>(() => CreateProxy(
            "ae01-main:8080",
            skipUpstreamTlsVerification: true));
    }

    [Fact]
    public void LegacyConfiguration_DefaultsTlsBypassToDisabled()
    {
        const string json = """
            {
              "schema": "route-v1",
              "pathPrefix": "/",
              "upstream": "ae01-main:8080",
              "preserveHost": false,
              "healthPath": "",
              "healthIntervalSeconds": 30,
              "redirectTarget": "",
              "redirectPermanent": true,
              "staticStatusCode": 200,
              "staticBody": "",
              "customSnippet": ""
            }
            """;

        var configuration = JsonSerializer.Deserialize<RouteConfigurationDocument>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(configuration);
        Assert.False(configuration.SkipUpstreamTlsVerification);
    }

    private static ManagedRouteDefinition CreateProxy(
        string upstream,
        bool skipUpstreamTlsVerification)
    {
        return ManagedRouteDefinition.Create(
            Guid.NewGuid(),
            "Service",
            Guid.NewGuid(),
            "example.com",
            "service",
            ManagedRouteKind.Proxy,
            true,
            0,
            RouteCertificateMode.Individual,
            null,
            RouteConfigurationDocument.Empty with
            {
                Upstream = upstream,
                PreserveHost = true,
                SkipUpstreamTlsVerification = skipUpstreamTlsVerification,
            });
    }
}
""",
)
