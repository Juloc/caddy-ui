from __future__ import annotations

import tempfile
import unittest
from dataclasses import replace
from pathlib import Path

from caddy_ui.audit import AuditLog
from caddy_ui.caddy import redact_config
from caddy_ui.db import Database
from caddy_ui.domain import HeaderOperation, ManagedRoute, Upstream
from caddy_ui.public_auth import (
    ADMIN_PROXY_HEADER,
    PORTAL_PROXY_HEADER,
    LoginCsrfMixin,
    PublicAuthCaddyManager,
    _public_host,
)
from tests.helpers import settings


class PublicAuthenticationTests(unittest.TestCase):
    def manager(self, directory: str, public_origin: str = "") -> PublicAuthCaddyManager:
        config = replace(settings(Path(directory)), public_origin=public_origin)
        database = Database(config)
        database.initialize()
        return PublicAuthCaddyManager(config, database, AuditLog(database))

    def test_public_origin_requires_standard_https(self) -> None:
        self.assertEqual(_public_host("https://Caddy.Example.com"), "caddy.example.com")
        for value in (
            "http://caddy.example.com",
            "https://caddy.example.com:8443",
            "https://user:pass@caddy.example.com",
            "https://caddy.example.com/path",
        ):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                _public_host(value)

    def test_public_admin_route_receives_generated_proxy_secret(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manager = self.manager(directory, "https://caddy.example.com")
            route = ManagedRoute(
                name="caddy-ui-admin",
                host="caddy.example.com",
                upstreams=[Upstream("caddy-ui:8098")],
            )

            rendered = "\n".join(manager._rendered_for([route]).values())

            self.assertIn(f"header_up {ADMIN_PROXY_HEADER} {manager.admin_secret}", rendered)
            self.assertNotIn(manager.admin_secret, redact_config(rendered))

    def test_public_admin_route_cannot_be_access_locked(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manager = self.manager(directory, "https://caddy.example.com")
            route = ManagedRoute(
                name="caddy-ui-admin",
                host="caddy.example.com",
                upstreams=[Upstream("caddy-ui:8098")],
                access_group_id="portal-group",
            )

            with self.assertRaisesRegex(ValueError, "exactly one enabled, unprotected"):
                manager._rendered_for([route])

    def test_reserved_authentication_headers_cannot_be_configured(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manager = self.manager(directory)
            for header in (ADMIN_PROXY_HEADER, PORTAL_PROXY_HEADER, "Remote-User", "X-Caddy-Portal-User"):
                route = ManagedRoute(
                    name="app",
                    domain="example.com",
                    upstreams=[Upstream("app:8080")],
                    request_headers=[HeaderOperation(header, "attacker")],
                )
                with self.subTest(header=header), self.assertRaisesRegex(ValueError, "Reserved authentication headers"):
                    manager._rendered_for([route])

    def test_internal_portal_listener_cannot_be_exposed_as_route(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manager = self.manager(directory)
            route = ManagedRoute(
                name="portal-backend",
                domain="example.com",
                upstreams=[Upstream("caddy-ui:8099")],
            )

            with self.assertRaisesRegex(ValueError, "internal access-portal listener"):
                manager._rendered_for([route])

    def test_normal_upstream_removes_internal_authentication_headers(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manager = self.manager(directory)
            route = ManagedRoute(
                name="app",
                domain="example.com",
                upstreams=[Upstream("app:8080")],
            )

            rendered = "\n".join(manager._rendered_for([route]).values())

            self.assertIn(f"header_up -{ADMIN_PROXY_HEADER}", rendered)
            self.assertIn(f"header_up -{PORTAL_PROXY_HEADER}", rendered)

    def test_login_csrf_is_inserted_into_the_login_form(self) -> None:
        content = b'<form method="post" action="/login"><button>Sign in</button></form>'
        secured = LoginCsrfMixin._inject_login_csrf(content, "csrf-token")
        self.assertIn(b'name="login_csrf" value="csrf-token"', secured)


if __name__ == "__main__":
    unittest.main()
