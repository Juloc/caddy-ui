from __future__ import annotations

import ipaddress
import socket
import subprocess
import urllib.parse
from functools import lru_cache
from typing import Any, Mapping

from . import protection
from .caddy import CaddyManager


PRIVATE_ALLOWLISTS = [
    "10.0.0.0/8",
    "172.16.0.0/12",
    "192.168.0.0/16",
    "127.0.0.0/8",
    "::1/128",
    "fc00::/7",
    "fe80::/10",
]


@lru_cache(maxsize=1)
def bundled_guard_available() -> bool:
    try:
        result = subprocess.run(
            ["/usr/bin/caddy", "list-modules"],
            check=False,
            capture_output=True,
            text=True,
            timeout=5,
        )
    except (OSError, subprocess.SubprocessError):
        return False
    return result.returncode == 0 and "http.handlers.caddy_ui_guard" in result.stdout.splitlines()


class RuntimeSecurityService(protection.SecurityService):
    """Adds exact trust for the Caddy container used by the managed forward-auth flow."""

    def __init__(self, *args: Any, **kwargs: Any):
        super().__init__(*args, **kwargs)
        self._internal_proxy_ips = self._resolve_internal_caddy()

    def _resolve_internal_caddy(self) -> set[str]:
        raw = str(self.database.setting("runtime_caddy_admin_url", "") or "")
        if not raw:
            return set()
        hostname = urllib.parse.urlsplit(raw).hostname
        if not hostname:
            return set()
        try:
            return {item[4][0] for item in socket.getaddrinfo(hostname, None)}
        except OSError:
            return set()

    def client_ip(self, peer_ip: str, headers: Mapping[str, str]) -> str:
        if peer_ip in self._internal_proxy_ips:
            forwarded = str(headers.get("X-Forwarded-For", ""))
            for value in reversed([item.strip() for item in forwarded.split(",") if item.strip()]):
                try:
                    return str(ipaddress.ip_address(value))
                except ValueError:
                    continue
            real_ip = str(headers.get("X-Real-IP", "")).strip()
            try:
                return str(ipaddress.ip_address(real_ip)) if real_ip else peer_ip
            except ValueError:
                return peer_ip
        return super().client_ip(peer_ip, headers)


class RuntimeSecurityCaddyManager(protection.SecurityCaddyManager):
    """Uses the custom directive only when the local bundle proves it is available."""

    def _rendered_for(self, routes):
        if not bundled_guard_available():
            return CaddyManager._rendered_for(self, routes)
        settings = protection.protection_settings(self.database)
        explicit = list(settings.get("allowlist", []))
        merged = dict(self.database.setting("protection", {}) or {})
        merged["allowlist"] = list(dict.fromkeys([*explicit, *PRIVATE_ALLOWLISTS]))
        original = self.database.setting("protection", {}) or {}
        self.database.set_setting("protection", merged)
        try:
            return super()._rendered_for(routes)
        finally:
            self.database.set_setting("protection", original)

    def apply_security_configuration(self, actor=None) -> str:
        if protection.protection_settings(self.database)["level"] != "off" and not bundled_guard_available():
            raise RuntimeError(
                "Integrated route protection requires the Caddy UI bundle image with http.handlers.caddy_ui_guard. "
                "Companion mode keeps existing routes unchanged."
            )
        return super().apply_security_configuration(actor)


def install() -> None:
    protection.SecurityService = RuntimeSecurityService
    protection.SecurityCaddyManager = RuntimeSecurityCaddyManager
