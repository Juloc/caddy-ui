from __future__ import annotations

import ipaddress
import os
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
    """Adds exact trust for the managed Caddy peer used by forward-auth."""

    def __init__(self, *args: Any, **kwargs: Any):
        super().__init__(*args, **kwargs)
        self._internal_proxy_ips = self._resolve_internal_caddy()

    @staticmethod
    def _resolve_internal_caddy() -> set[str]:
        hostname = urllib.parse.urlsplit(os.getenv("CADDY_ADMIN_URL", "http://caddy:2019")).hostname
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
    """Skips custom directives when the running image does not contain the guard module."""

    def _rendered_for(self, routes):
        if not bundled_guard_available():
            return CaddyManager._rendered_for(self, routes)
        return super()._rendered_for(routes)

    def _guard_directive(self, values: dict[str, int], settings: dict[str, Any]) -> str:
        runtime_settings = dict(settings)
        runtime_settings["allowlist"] = list(
            dict.fromkeys([*settings.get("allowlist", []), *PRIVATE_ALLOWLISTS])
        )
        return super()._guard_directive(values, runtime_settings)

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
