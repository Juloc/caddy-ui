from __future__ import annotations

import ipaddress
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from caddy_ui.audit import AuditLog
from caddy_ui.db import Database
from caddy_ui.domain import ManagedRoute, Upstream
from caddy_ui.secure_caddy import HardenedCaddyManager, render_hardened_site
from caddy_ui.security_policy import (
    PersistentLoginThrottle,
    SecurityPolicy,
    normalize_host,
    safe_return_path,
)
from tests.helpers import settings


def policy(public_url: str = "", public_host: str = "", secret: str = "s" * 48) -> SecurityPolicy:
    return SecurityPolicy(
        public_url=public_url,
        public_scheme="https" if public_url else "",
        public_host=public_host,
        trusted_proxy_networks=(ipaddress.ip_network("127.0.0.0/8"),),
        proxy_secret=secret,
        require_totp=bool(public_url),
        bind_session_ip=False,
        portal_session_ttl_seconds=28800,
        throttle_window_seconds=900,
        account_attempts=2,
        address_attempts=3,
    )


class SecureAuthTests(unittest.TestCase):
    def protected_route(self) -> ManagedRoute:
        return ManagedRoute(
            name="api",
            domain="example.com",
            paths=["/api/*"],
            upstreams=[Upstream("app:8080")],
            access_group_id="group-1",
        )

    def test_path_route_serves_portal_outside_path_matcher(self) -> None:
        value = render_hardened_site("api.example.com", [self.protected_route()])
        portal = value.index("handle /__caddy_ui_auth/*")
        route = value.index("path /api/*")
        self.assertLess(portal, route)
        self.assertIn("header_up X-Caddy-UI-Proxy-Secret {env.CADDY_UI_PROXY_SECRET}", value)
        self.assertIn("request_header -Remote-User", value)
        self.assertIn("forward_auth caddy-ui:8098", value)

    def test_public_ui_route_requires_explicit_matching_origin(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            route = ManagedRoute(
                name="caddy",
                domain="example.com",
                host="caddy.example.com",
                upstreams=[Upstream("caddy-ui:8098")],
            )
            private_manager = HardenedCaddyManager(config, database, AuditLog(database), policy())
            with self.assertRaisesRegex(ValueError, "CADDY_UI_PUBLIC_URL"):
                private_manager.preview(proposed=route)
            wrong_manager = HardenedCaddyManager(
                config,
                database,
                AuditLog(database),
                policy("https://other.example.com", "other.example.com"),
            )
            with self.assertRaisesRegex(ValueError, "must match"):
                wrong_manager.preview(proposed=route)
            manager = HardenedCaddyManager(
                config,
                database,
                AuditLog(database),
                policy("https://caddy.example.com", "caddy.example.com"),
            )
            rendered, _ = manager.preview(proposed=route)
            self.assertIn("header_up X-Caddy-UI-Proxy-Secret", rendered)

    def test_login_throttle_survives_new_instance(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            first = PersistentLoginThrottle(database, policy())
            limits = (("admin:account:user", 2),)
            self.assertTrue(first.allowed(limits))
            first.record_failure(("admin:account:user",))
            first.record_failure(("admin:account:user",))
            second = PersistentLoginThrottle(database, policy())
            self.assertFalse(second.allowed(limits))
            with database.connect() as connection:
                columns = {row[1] for row in connection.execute("PRAGMA table_info(portal_sessions)")}
            self.assertTrue({"created_at", "remote_address", "user_agent"}.issubset(columns))

    def test_sessions_are_invalidated_when_security_context_changes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            current_policy = policy("https://caddy.example.com", "caddy.example.com", "a" * 48)
            PersistentLoginThrottle(database, current_policy)
            admin = database.authenticate("admin", "correct-horse-battery-staple")
            self.assertIsNotNone(admin)
            token, _ = database.create_session(admin["id"], 3600, "127.0.0.1", "test")
            PersistentLoginThrottle(database, current_policy)
            self.assertIsNotNone(database.session(token))
            changed_policy = policy("https://caddy.example.com", "caddy.example.com", "b" * 48)
            PersistentLoginThrottle(database, changed_policy)
            self.assertIsNone(database.session(token))

    def test_return_path_rejects_external_redirects(self) -> None:
        self.assertEqual(safe_return_path("//evil.example/path"), "/")
        self.assertEqual(safe_return_path("/\\evil"), "/")
        self.assertEqual(safe_return_path("https://evil.example"), "/")
        self.assertEqual(safe_return_path("/api/items?page=2#fragment"), "/api/items?page=2")

    def test_malformed_hosts_and_nonstandard_public_ports_are_rejected(self) -> None:
        self.assertEqual(normalize_host("example.com:not-a-port"), "")
        environment = {
            "CADDY_UI_PUBLIC_URL": "https://caddy.example.com:8443",
            "CADDY_UI_PROXY_SECRET": "s" * 48,
            "CADDY_UI_TRUSTED_PROXY_CIDRS": "127.0.0.0/8",
        }
        with patch.dict(os.environ, environment, clear=True):
            with self.assertRaisesRegex(RuntimeError, "standard HTTPS origin"):
                SecurityPolicy.from_environment()


if __name__ == "__main__":
    unittest.main()
