from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from caddy_ui.audit import AuditLog
from caddy_ui.db import Database
from caddy_ui.domain import ManagedRoute, Upstream
from caddy_ui.hardened_caddy import HardenedSecurityCaddyManager, render_route, render_site
from caddy_ui.hardened_web import _normalized_origin, _safe_return_to
from tests.helpers import settings


class TestManager(HardenedSecurityCaddyManager):
    def validate(self) -> None:
        return None

    def reload(self) -> None:
        return None


class HardenedAuthenticationTests(unittest.TestCase):
    def route(self, protected: bool = True) -> ManagedRoute:
        return ManagedRoute(
            name="api",
            domain="example.com",
            paths=["/api/*"],
            upstreams=[Upstream("app:8080")],
            access_group_id="portal-group" if protected else "",
        )

    def test_portal_handler_precedes_path_route_and_guard(self) -> None:
        value = render_site("api.example.com", [self.route()], "portal-secret")
        self.assertLess(value.index("handle @caddy_ui_portal"), value.index("forward_auth caddy-ui:8099"))
        self.assertIn("reverse_proxy caddy-ui:8099", value)
        self.assertNotIn("handle /__caddy_ui_auth/*", value)

    def test_security_guard_and_portal_auth_are_combined(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            manager = HardenedSecurityCaddyManager(config, database, AuditLog(database))
            rendered, _ = manager.preview(proposed=self.route())
            self.assertIn("caddy_ui_guard", rendered)
            self.assertIn("forward_auth caddy-ui:8099", rendered)

    def test_protected_route_overwrites_identity_headers(self) -> None:
        value = render_route(self.route(), "portal-secret")
        self.assertIn("copy_headers Remote-User>X-Caddy-Portal-User", value)
        self.assertIn("header_up Remote-User {http.request.header.X-Caddy-Portal-User}", value)
        self.assertIn("header_up -X-Caddy-Portal-User", value)

    def test_unprotected_route_removes_spoofable_identity_headers(self) -> None:
        value = render_route(self.route(protected=False), "portal-secret")
        self.assertIn("header_up -Remote-User", value)
        self.assertIn("header_up -X-Caddy-Portal-User", value)

    def test_reserved_portal_path_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            manager = HardenedSecurityCaddyManager(config, database, AuditLog(database))
            route = self.route()
            route.paths = ["/__caddy_ui_auth/*"]
            with self.assertRaisesRegex(ValueError, "reserved access portal prefix"):
                manager.preview(proposed=route)

    def test_preview_redacts_internal_portal_secret(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            manager = HardenedSecurityCaddyManager(config, database, AuditLog(database))
            rendered, diff = manager.preview(proposed=self.route())
            self.assertNotIn(manager.portal_secret, rendered)
            self.assertNotIn(manager.portal_secret, diff)
            self.assertIn("[redacted]", rendered)

    def test_startup_reconcile_replaces_old_auth_rendering(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            manager = TestManager(config, database, AuditLog(database))
            manager.routes.save(self.route())
            config.routes_dir.mkdir(parents=True, exist_ok=True)
            legacy = config.routes_dir / "site-old.caddy"
            legacy.write_text("# managed-by caddy-ui\nold insecure auth\n", encoding="utf-8")
            self.assertTrue(manager.reconcile())
            value = next(config.routes_dir.glob("site-*.caddy")).read_text(encoding="utf-8")
            self.assertIn("forward_auth caddy-ui:8099", value)
            self.assertIn("caddy_ui_guard", value)
            self.assertFalse(legacy.exists())

    def test_return_target_cannot_loop_or_leave_current_host(self) -> None:
        self.assertEqual(_safe_return_to("/__caddy_ui_auth/login?group=x"), "/")
        self.assertEqual(_safe_return_to("https://evil.example/path"), "/")
        self.assertEqual(_safe_return_to("//evil.example/path"), "/")
        self.assertEqual(_safe_return_to("/api/items?state=open"), "/api/items?state=open")

    def test_public_origin_requires_clean_origin_value(self) -> None:
        self.assertEqual(_normalized_origin("https://Caddy.Example.com/"), "https://caddy.example.com")
        self.assertEqual(_normalized_origin("https://caddy.example.com/path"), "")
        self.assertEqual(_normalized_origin("https://user:pass@caddy.example.com"), "")


if __name__ == "__main__":
    unittest.main()
