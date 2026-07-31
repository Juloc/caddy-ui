#!/usr/bin/env python3
"""Migrate legacy page-model feedback strings to shared localization keys."""

from __future__ import annotations

from pathlib import Path
import ast
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
RESX = ROOT / "src/CaddyUi.Web/Resources/SharedResource.de.resx"
PAGES = ROOT / "src/CaddyUi.Web/Pages"


def resource_aliases() -> dict[str, str]:
    root = ET.parse(RESX).getroot()
    aliases: dict[str, str] = {}
    for data in root.findall("data"):
        key = data.attrib.get("name")
        value = data.findtext("value")
        if key and value and value != key:
            aliases[value] = key
    return aliases


def quote(value: str) -> str:
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"') + '"'


def decode_literal(token: str) -> str | None:
    try:
        value = ast.literal_eval(token)
    except (SyntaxError, ValueError):
        return None
    return value if isinstance(value, str) else None


def migrate_base_class(content: str) -> str:
    # Direct page models and the analytics base share the localization helper.
    return re.sub(
        r"(public\s+(?:abstract\s+)?(?:sealed\s+)?class\s+\w+\s*:\s*)PageModel\b",
        r"\1LocalizedPageModel",
        content,
    )


def migrate_fixed_literals(content: str, aliases: dict[str, str]) -> str:
    # Only rewrite message-producing contexts. Attribute arguments and storage
    # values remain compile-time constants and are intentionally untouched.
    contexts = [
        r"(?P<prefix>TempData\[[^\n=]+\]\s*=\s*)",
        r"(?P<prefix>StatusMessage\s*=\s*)",
        r"(?P<prefix>LoadError\s*=\s*)",
        r"(?P<prefix>ModelState\.AddModelError\([^,]+,\s*)",
        r"(?P<prefix>throw\s+new\s+(?:ArgumentException|InvalidOperationException)\s*\(\s*)",
    ]
    literal = r'(?P<literal>"(?:\\.|[^"\\])*")'
    suffix = r"(?P<suffix>\s*\)?)"

    for context in contexts:
        pattern = re.compile(context + literal + suffix)

        def replace_match(match: re.Match[str]) -> str:
            value = decode_literal(match.group("literal"))
            key = aliases.get(value or "")
            if not key:
                return match.group(0)
            return f'{match.group("prefix")}Text({quote(key)}){match.group("suffix")}'

        content = pattern.sub(replace_match, content)
    return content


def migrate_simple_interpolations(content: str, aliases: dict[str, str]) -> str:
    # Handles the common one-value feedback pattern: $"text {value}".
    pattern = re.compile(
        r'(?P<prefix>(?:TempData\[[^\n=]+\]|StatusMessage|LoadError)\s*=\s*)'
        r'\$"(?P<body>(?:\\.|[^"\\])*)"'
    )

    def replace_match(match: re.Match[str]) -> str:
        body = match.group("body")
        expressions = re.findall(r"\{([^{}]+)\}", body)
        if not expressions:
            return match.group(0)
        normalized = body
        for index, expression in enumerate(expressions):
            normalized = normalized.replace("{" + expression + "}", "{" + str(index) + "}", 1)
        key = aliases.get(normalized)
        if not key:
            return match.group(0)
        arguments = ", ".join(expressions)
        return f'{match.group("prefix")}Text({quote(key)}, {arguments})'

    return pattern.sub(replace_match, content)


def migrate_file(path: Path, aliases: dict[str, str]) -> None:
    content = path.read_text(encoding="utf-8-sig")
    content = migrate_base_class(content)
    content = migrate_fixed_literals(content, aliases)
    content = migrate_simple_interpolations(content, aliases)
    path.write_text(content, encoding="utf-8", newline="\n")


def main() -> None:
    aliases = resource_aliases()
    for path in PAGES.rglob("*.cs"):
        if path.name == "LocalizedPageModel.cs":
            continue
        migrate_file(path, aliases)


if __name__ == "__main__":
    main()
