from __future__ import annotations

from typing import Any

from . import protection


PRESETS: dict[str, dict[str, int]] = {
    "off": {"requests": 0, "window_seconds": 60, "burst": 0, "block_seconds": 0},
    "balanced": {"requests": 300, "window_seconds": 60, "burst": 60, "block_seconds": 900},
    "strict": {"requests": 120, "window_seconds": 60, "burst": 20, "block_seconds": 900},
}


def protection_settings(database) -> dict[str, Any]:
    raw = database.setting("protection", {}) or {}
    level = str(raw.get("level", "balanced")).lower()
    if level not in protection.PROTECTION_LEVELS:
        level = "balanced"

    global_raw = raw.get("global", {}) if isinstance(raw.get("global"), dict) else {}
    if level in PRESETS:
        global_values = dict(PRESETS[level])
    else:
        global_values = {
            "requests": max(1, int(global_raw.get("requests", 300) or 300)),
            "window_seconds": max(1, int(global_raw.get("window_seconds", 60) or 60)),
            "burst": max(0, int(global_raw.get("burst", 60) or 0)),
            "block_seconds": max(60, int(global_raw.get("block_seconds", 900) or 900)),
        }

    login_raw = raw.get("login", {}) if isinstance(raw.get("login"), dict) else {}
    return {
        "level": level,
        "global": global_values,
        "login": {
            "delay_after": max(1, int(login_raw.get("delay_after", 5) or 5)),
            "block_after": max(2, int(login_raw.get("block_after", 10) or 10)),
            "window_seconds": max(60, int(login_raw.get("window_seconds", 900) or 900)),
        },
        "trusted_proxies": [str(item).strip() for item in raw.get("trusted_proxies", []) if str(item).strip()],
        "allowlist": [str(item).strip() for item in raw.get("allowlist", []) if str(item).strip()],
        "route_overrides": raw.get("route_overrides", {}) if isinstance(raw.get("route_overrides"), dict) else {},
    }


def install() -> None:
    protection.protection_settings = protection_settings
