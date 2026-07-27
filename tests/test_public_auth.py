from __future__ import annotations

import tempfile
import unittest
from dataclasses import replace
from pathlib import Path

from caddy_ui.audit import AuditLog
from caddy_ui.caddy import redact_config
from caddy_ui.db import Database
from caddy_ui.domain import ManagedRoute, Upstream
from caddy_ui.public_auth import (
    ADMIN_PROXY_HEADER,
    LoginCsrfMixin,
    PublicAuthCaddyManager,
    _public_host,
)
from tests.helpers import settings


class PublicAuthenticationTests(unittest.TestCase):
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
            config = replace(
                settings(Path(directory)),
                public_origin="https://caddy.example.com",
            )
            database = Database(config)
            database.initialize()
            manager = PublicAuthCaddyManager(config, database, AuditLog(database))
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
            config = replace(
                settings(Path(directory)),
                public_origin="https://caddy.example.com",
            )
            database = Database(config)
            database.initialize()
            manager = PublicAuthCaddyManager(config, database, AuditLog(database))
            route = ManagedRoute(
                name="caddy-ui-admin",
                host="caddy.example.com",
                upstreams=[Upstream("caddy-ui:8098")],
                access_group_id="portal-group",
            )

            with self.assertRaisesRegex(ValueError, "exactly one enabled, unprotected"):
                manager._rendered_for([route])

    def test_login_csrf_is_inserted_into_the_login_form(self) -> None:
        content = b'<form method="post" action="/login"><button>Sign in</button></form>'
        secured = LoginCsrfMixin._inject_login_csrf(content, "csrf-token")
        self.assertIn(b'name="login_csrf" value="csrf-token"', secured)


if __name__ == "__main__":
    unittest.main()
