from __future__ import annotations

import tempfile
import unittest
from dataclasses import replace
from pathlib import Path

from caddy_ui.audit import AuditLog
from caddy_ui.caddy import redact_config
from caddy_ui.db import Database
from caddy_ui.domain import HeaderOperation, ManagedRoute, RouteKind, Upstream
from caddy_ui.public_auth import (
    ADMIN_PROXY_HEADER,
    PORTAL_PROXY_HEADER,
    LoginCsrfMixin,
    PublicAuthCaddyManager,
    _public_host,
)
from caddy_ui.public_auth_hotfix import MigratingPublicAuthCaddyManager, public_settings
from tests.helpers import settings


class PublicAuthenticationTests(unittest.TestCase):
    def manager(self, directory: str, public_origin: str = "") -> PublicAuthCaddyManager:
        config = replace(settings(Path(directory)), public_origin=public_origin)
        database = Database(config)
        database.initialize()
        return PublicAuthCaddyManager(config, database, AuditLog(database))

    def migrating_manager(self, directory: str, public_origin: str) -> MigratingPublicAuthCaddyManager:
        config = replace(settings(Path(directory)), public_origin=public_origin)
        database = Database(config)
        database.initialize()
        return MigratingPublicAuthCaddyManager(config, database, AuditLog(database))

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

    def test_single_legacy_public_route_is_fully_normalized(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manager = self.migrating_manager(directory, "https://caddy.example.com")
            route = ManagedRoute(
                name="legacy-admin",
                host="caddy.example.com",
                kind=RouteKind.PROXY,
                enabled=False,
                paths=["/admin/*"],
                upstreams=[Upstream("host.docker.internal:8098")],
                request_headers=[HeaderOperation("X-Legacy", "true")],
                response_headers=[HeaderOperation("X-Legacy-Response", "true")],
                load_balancing="round_robin",
                health_uri="/health",
                tls_skip_verify=True,
                access_group_id="legacy-portal",
            )
            manager.routes.save(route)
            applied: dict[str, object] = {}

            def capture_apply(actor, reason, proposed=None, delete_id=""):
                applied["actor"] = actor
                applied["reason"] = reason
                applied["route"] = proposed
                return "revision"

            manager.apply = capture_apply  # type: ignore[method-assign]

            self.assertTrue(manager.ensure_public_route())
            migrated = applied["route"]
            self.assertIsInstance(migrated, ManagedRoute)
            self.assertEqual(migrated.id, route.id)
            self.assertEqual(migrated.name, route.name)
            self.assertEqual(migrated.host, "caddy.example.com")
            self.assertEqual(migrated.domain, "")
            self.assertEqual(migrated.kind, RouteKind.PROXY)
            self.assertTrue(migrated.enabled)
            self.assertEqual(migrated.paths, [])
            self.assertEqual([item.address for item in migrated.upstreams], ["caddy-ui:8098"])
            self.assertEqual(migrated.request_headers, [])
            self.assertEqual(migrated.response_headers, [])
            self.assertEqual(migrated.access_group_id, "")
            self.assertFalse(migrated.tls_skip_verify)
            self.assertIn("Normalize public Caddy UI route", str(applied["reason"]))

    def test_multiple_public_routes_still_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manager = self.migrating_manager(directory, "https://caddy.example.com")
            manager.routes.save(
                ManagedRoute(name="admin-one", host="caddy.example.com", upstreams=[Upstream("one:8080")])
            )
            manager.routes.save(
                ManagedRoute(name="admin-two", host="caddy.example.com", upstreams=[Upstream("two:8080")])
            )

            with self.assertRaisesRegex(ValueError, "exactly one managed route"):
                manager.ensure_public_route()

    def test_public_settings_do_not_force_totp_before_enrollment(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = replace(
                settings(Path(directory)),
                public_origin="https://caddy.example.com",
                require_totp=False,
                secure_cookies=False,
                session_ttl_seconds=86_400,
            )

            secured = public_settings(config)

            self.assertTrue(secured.secure_cookies)
            self.assertFalse(secured.require_totp)
            self.assertEqual(secured.session_ttl_seconds, 28_800)

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
