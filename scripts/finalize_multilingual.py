#!/usr/bin/env python3
"""Finalize the multilingual UI branch and remove temporary inconsistencies.

The script is intentionally deterministic and idempotent. It is run once by the
branch-only workflow, then removed together with that workflow before the final
commit is pushed.
"""

from __future__ import annotations

from collections import OrderedDict
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path: str, content: str) -> None:
    target = ROOT / path
    target.write_text(content, encoding="utf-8", newline="\n")


def replace(path: str, old: str, new: str, *, required: bool = True) -> None:
    content = read(path)
    if old not in content:
        if required:
            raise RuntimeError(f"Expected text not found in {path}: {old[:100]!r}")
        return
    write(path, content.replace(old, new))


def deduplicate_resx(path: str) -> None:
    target = ROOT / path
    tree = ET.parse(target)
    root = tree.getroot()
    headers = [child for child in root if child.tag == "resheader"]
    assembly = [child for child in root if child.tag == "assembly"]
    metadata = [child for child in root if child.tag == "metadata"]
    data_by_name: OrderedDict[str, ET.Element] = OrderedDict()
    for child in root:
        if child.tag != "data":
            continue
        name = child.attrib.get("name")
        if not name:
            continue
        # Keep the latest translation so later corrections win deterministically.
        data_by_name[name] = child

    clean = ET.Element("root")
    for child in headers + assembly + metadata:
        clean.append(child)
    for name in sorted(data_by_name, key=str.casefold):
        clean.append(data_by_name[name])

    ET.indent(clean, space="  ")
    xml = ET.tostring(clean, encoding="unicode")
    write(path, '<?xml version="1.0" encoding="utf-8"?>\n' + xml + "\n")


def wire_certificate_localization() -> None:
    path = "src/CaddyUi.Web/Pages/Administration/Domains.cshtml"
    content = read(path)
    if "@using CaddyUi.Web.Localization" not in content:
        content = content.replace("@page\n", "@page\n@using CaddyUi.Web.Localization\n", 1)

    replacements = {
        "@certificate.Label": "@CertificateUiText.CertificateLabel(T, certificate)",
        "@certificate.Detail": "@CertificateUiText.CertificateDetail(T, certificate)",
        "@lifecycle.Label": "@CertificateUiText.LifecycleLabel(T, lifecycle)",
        "@DomainsModel.CurrentAction(lifecycle)": "@CertificateUiText.CurrentAction(T, lifecycle)",
        "@DomainsModel.CurrentActionDetail(lifecycle, timing)": "@CertificateUiText.CurrentActionDetail(T, lifecycle, timing?.PropagationDelaySeconds, timing?.PropagationTimeoutSeconds)",
        "@DomainsModel.ProviderTestLabel(lifecycle.ProviderTestStatus)": "@CertificateUiText.ProviderTestLabel(T, lifecycle.ProviderTestStatus)",
        "@attempt.Label": "@CertificateUiText.AttemptLabel(T, attempt.State)",
        "@item.Label": "@CertificateUiText.EventLabel(T, item.State)",
        "@item.Detail": "@CertificateUiText.EventDetail(T, item)",
        "@DomainsModel.FormatDuration(timing?.PropagationDelaySeconds)": "@CertificateUiText.FormatDuration(T, timing?.PropagationDelaySeconds)",
        "@DomainsModel.FormatDuration(timing?.PropagationTimeoutSeconds ?? lifecycle.PropagationTimeoutSeconds)": "@CertificateUiText.FormatDuration(T, timing?.PropagationTimeoutSeconds ?? lifecycle.PropagationTimeoutSeconds)",
        "@DomainsModel.FormatDuration(lifecycle.DnsTtlSeconds)": "@CertificateUiText.FormatDuration(T, lifecycle.DnsTtlSeconds)",
    }
    for old, new in replacements.items():
        content = content.replace(old, new)

    marker = "var attempts = DomainsModel.GroupAttempts(lifecycle);"
    if "var localizedTips = CertificateUiText.Tips(T, lifecycle);" not in content:
        content = content.replace(
            marker,
            marker + "\n                                        var localizedTips = CertificateUiText.Tips(T, lifecycle);",
        )
    content = content.replace("lifecycle.Tips.Count", "localizedTips.Count")
    content = content.replace("foreach (var tip in lifecycle.Tips)", "foreach (var tip in localizedTips)")
    write(path, content)


def normalize_provider_reader() -> None:
    path = "src/CaddyUi.Infrastructure/Operations/DnsProviderRecordQueryService.cs"
    content = read(path)
    content = content.replace(
        "        catch (Exception exception) when (exception is not OperationCanceledException)\n        {\n        }",
        "        catch (OperationCanceledException)\n        {\n            throw;\n        }\n        catch\n        {\n            // Logout is best effort and must not hide the result of the main operation.\n        }",
    )
    write(path, content)


def localize_status_rendering() -> None:
    # Fixed messages stored in TempData/StatusMessage are resource keys. Rendering
    # through the shared localizer keeps old and new page models culture-aware.
    for target in (ROOT / "src/CaddyUi.Web/Pages").rglob("*.cshtml"):
        content = target.read_text(encoding="utf-8-sig")
        content = re.sub(r">@Model\.StatusMessage<", ">@T[Model.StatusMessage]<", content)
        content = re.sub(r">@message<", ">@T[message]<", content)
        # Provider/Caddy errors can be external text. Missing resource keys fall
        # back to the original string without altering diagnostics.
        content = re.sub(r">@error<", ">@T[error]<", content)
        content = re.sub(r">@operationError<", ">@T[operationError]<", content)
        target.write_text(content, encoding="utf-8", newline="\n")


def english_first_page_messages() -> None:
    # Convert legacy German feedback keys to English. Razor views localize these
    # keys through SharedResource, while raw provider errors remain untouched.
    translations = {
        "Route aktiviert.": "Route enabled.",
        "Route deaktiviert.": "Route disabled.",
        "Route gelöscht. Die aktive Caddy-Konfiguration ändert sich erst nach Vorschau und Apply.": "Route deleted. The active Caddy configuration changes only after preview and apply.",
        "Zugriffsgruppe wurde angelegt.": "Access group created.",
        "Zugriffsgruppe aktiviert.": "Access group enabled.",
        "Zugriffsgruppe deaktiviert.": "Access group disabled.",
        "Portal-Zugang wurde angelegt. Das Passwort wird nicht wieder angezeigt.": "Portal credential created. The password is not displayed again.",
        "Portal-Zugang aktiviert.": "Portal credential enabled.",
        "Portal-Zugang deaktiviert.": "Portal credential disabled.",
        "DNS-Eintrag wurde als verwalteter Entwurf angelegt.": "DNS record created as a managed draft.",
        "DNS-Eintrag aktiviert.": "DNS record enabled.",
        "DNS-Eintrag deaktiviert.": "DNS record disabled.",
        "DDNS-Ziel wurde angelegt.": "DDNS target created.",
        "DDNS-Ziel aktiviert.": "DDNS target enabled.",
        "DDNS-Ziel deaktiviert.": "DDNS target disabled.",
    }
    for target in (ROOT / "src/CaddyUi.Web/Pages").rglob("*.cs"):
        content = target.read_text(encoding="utf-8-sig")
        for german, english in translations.items():
            content = content.replace(f'"{german}"', f'"{english}"')
        target.write_text(content, encoding="utf-8", newline="\n")


def update_auth_contract() -> None:
    path = "tests/CaddyUi.Web.Tests/LoginPageTests.cs"
    content = read(path)
    for route in ("/Settings", "/Administration/ProviderDns"):
        if f'"{route}"' in content:
            continue
        anchor = '            "/Administration/Providers",\n'
        if anchor not in content:
            raise RuntimeError("Protected-page route list anchor was not found")
        content = content.replace(anchor, anchor + f'            "{route}",\n', 1)
    write(path, content)


def add_contract_tests() -> None:
    path = ROOT / "tests/CaddyUi.Web.Tests/MultilingualAndRouteEditorContractTests.cs"
    path.write_text(
        '''using System.Reflection;\nusing CaddyUi.Web.Localization;\nusing CaddyUi.Web.Pages.Routing;\n\nnamespace CaddyUi.Web.Tests;\n\npublic sealed class MultilingualAndRouteEditorContractTests\n{\n    [Fact]\n    public void CultureCatalog_DefaultsToEnglishAndSupportsGerman()\n    {\n        var catalog = new UiCultureCatalog(\n            Microsoft.Extensions.Options.Options.Create(\n                new UiCultureOptions\n                {\n                    DefaultCulture = "en",\n                    SupportedCultures = ["en", "de"],\n                }));\n\n        Assert.Equal("en", catalog.DefaultCulture);\n        Assert.Equal("en", catalog.Normalize(null));\n        Assert.Equal(["en", "de"], catalog.SupportedCultures.Select(item => item.Name));\n    }\n\n    [Fact]\n    public void RouteEditor_ExposesSeparateSaveAndSaveApplyHandlers()\n    {\n        var methods = typeof(EditModel)\n            .GetMethods(BindingFlags.Instance | BindingFlags.Public)\n            .Select(method => method.Name)\n            .ToHashSet(StringComparer.Ordinal);\n\n        Assert.Contains("OnPostSaveAsync", methods);\n        Assert.Contains("OnPostSaveApplyAsync", methods);\n    }\n\n    [Fact]\n    public void RouteEditor_ViewContainsBothExplicitActions()\n    {\n        var source = File.ReadAllText(RepositoryFile("src/CaddyUi.Web/Pages/Routing/Edit.cshtml"));\n\n        Assert.Contains("asp-page-handler=\\\"Save\\\"", source, StringComparison.Ordinal);\n        Assert.Contains("asp-page-handler=\\\"SaveApply\\\"", source, StringComparison.Ordinal);\n        Assert.Contains("Save and activate", source, StringComparison.Ordinal);\n        Assert.Contains("Save and update", source, StringComparison.Ordinal);\n    }\n\n    [Fact]\n    public void GermanResource_DoesNotContainDuplicateKeys()\n    {\n        var document = System.Xml.Linq.XDocument.Load(\n            RepositoryFile("src/CaddyUi.Web/Resources/SharedResource.de.resx"));\n        var keys = document.Root!\n            .Elements("data")\n            .Select(element => (string?)element.Attribute("name"))\n            .Where(name => !string.IsNullOrWhiteSpace(name))\n            .ToArray();\n\n        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());\n    }\n\n    private static string RepositoryFile(string relativePath)\n    {\n        var directory = new DirectoryInfo(AppContext.BaseDirectory);\n        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CaddyUi.slnx")))\n        {\n            directory = directory.Parent;\n        }\n\n        Assert.NotNull(directory);\n        return Path.Combine(directory!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));\n    }\n}\n''',
        encoding="utf-8",
        newline="\n",
    )


def update_version() -> None:
    write("VERSION_DOTNET", "2.2.0\n")
    candidates = [
        "Dockerfile.dotnet",
        "src/CaddyUi.Domain/ProductMetadata.cs",
        "tests/CaddyUi.Domain.Tests/ProductMetadataTests.cs",
    ]
    for path in candidates:
        content = read(path)
        content = re.sub(r"2\.1\.6", "2.2.0", content)
        write(path, content)


def main() -> None:
    deduplicate_resx("src/CaddyUi.Web/Resources/SharedResource.de.resx")
    wire_certificate_localization()
    normalize_provider_reader()
    localize_status_rendering()
    english_first_page_messages()
    update_auth_contract()
    add_contract_tests()
    update_version()


if __name__ == "__main__":
    main()
