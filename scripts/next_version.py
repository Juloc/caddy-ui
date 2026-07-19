#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


VERSION_RE = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(alpha|beta)\.(0|[1-9]\d*))?$")


def next_version(current: str, labels: set[str]) -> str:
    match = VERSION_RE.fullmatch(current.strip())
    if not match:
        raise ValueError(f"Unsupported version: {current}")
    major, minor, patch = (int(match.group(index)) for index in (1, 2, 3))
    stage, sequence_value = match.group(4), match.group(5)
    sequence = int(sequence_value or 0)
    names = {label.lower() for label in labels}

    requested_stage = "stable" if names & {"stable", "release:stable"} else "beta" if names & {"beta", "release:beta"} else stage or "stable"
    bump = "major" if names & {"major", "release:major"} else "minor" if names & {"minor", "release:minor"} else "patch"

    if bump == "major":
        major, minor, patch = major + 1, 0, 0
        return f"{major}.{minor}.{patch}" if requested_stage == "stable" else f"{major}.{minor}.{patch}-{requested_stage or 'alpha'}.1"
    if bump == "minor":
        minor, patch = minor + 1, 0
        return f"{major}.{minor}.{patch}" if requested_stage == "stable" else f"{major}.{minor}.{patch}-{requested_stage or 'alpha'}.1"

    if stage:
        if requested_stage == "stable":
            return f"{major}.{minor}.{patch}"
        if requested_stage != stage:
            return f"{major}.{minor}.{patch}-{requested_stage}.1"
        return f"{major}.{minor}.{patch}-{stage}.{sequence + 1}"
    patch += 1
    return f"{major}.{minor}.{patch}" if requested_stage in {None, "stable"} else f"{major}.{minor}.{patch}-{requested_stage}.1"


def labels_from_json(value: str) -> set[str]:
    raw = json.loads(value or "[]")
    return {str(item.get("name", "")) for item in raw if isinstance(item, dict)}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version-file", type=Path, default=Path("caddy_ui/VERSION"))
    parser.add_argument("--current")
    parser.add_argument("--labels-json", default="[]")
    parser.add_argument("--write", action="store_true")
    arguments = parser.parse_args()
    current = arguments.current or arguments.version_file.read_text(encoding="utf-8").strip()
    value = next_version(current, labels_from_json(arguments.labels_json))
    if arguments.write:
        arguments.version_file.write_text(value + "\n", encoding="utf-8")
    print(value)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
