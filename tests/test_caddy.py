from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from caddy_ui.audit import Actor, AuditLog
from caddy_ui.caddy import DEFAULT_CADDYFILE, CaddyManager, render_route, render_site
from caddy_ui.db import Database
from caddy_ui.domain import HeaderOperation, ManagedRoute, RouteKind, Upstream
from tests.helpers import settings


class TestManager(CaddyManager):
    validation_error = ""

    def validate(self) -> None:
        if self.validation_error:
            raise RuntimeError(self.validation_error)

    def reload(self) -> None:
        return None


class CaddyTests(unittest.TestCase):
    def route(self) -> ManagedRoute:
        return ManagedRoute(
            name="app",
            domain="example.com",
            paths=["/api/*"],
            upstreams=[Upstream("app:8080"), Upstream("app-2:8080")],
            request_headers=[HeaderOperation("X-Forwarded-Test", "true")],
            response_headers=[HeaderOperation("X-Frame-Options", "DENY")],
            load_balancing="round_robin",
            health_uri="/health",
        )

    def test_rendered_route_contains_advanced_proxy_features(self) -> None:
        value = render_route(self.route())
        self.assertIn("path /api/*", value)
        self.assertIn("reverse_proxy app:8080 app-2:8080", value)
        self.assertIn("lb_policy round_robin", value)
        self.assertIn("health_uri /health", value)
        self.assertIn("X-Forwarded-Test true", value)
        site = render_site("app.example.com", [self.route()])
        self.assertIn("app.example.com {", site)
        self.assertIn("respond \"Service not configured\" 404", site)

    def test_apply_records_revision_and_rolls_back_database_and_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            manager = TestManager(config, database, AuditLog(database))
            route = self.route()
            revision = manager.apply(Actor(username="admin"), "create", proposed=route)
            self.assertTrue(revision)
            self.assertIsNotNone(manager.routes.get(route.id))
            self.assertEqual(len(list(config.routes_dir.glob("site-*.caddy"))), 1)
            changed = ManagedRoute.from_json(route.to_json())
            changed.upstreams = [Upstream("changed:8080")]
            manager.validation_error = "invalid"
            with self.assertRaises(RuntimeError):
                manager.apply(Actor(username="admin"), "change", proposed=changed)
            self.assertEqual(manager.routes.get(route.id).upstreams[0].address, "app:8080")
            content = next(config.routes_dir.glob("site-*.caddy")).read_text(encoding="utf-8")
            self.assertIn("app:8080", content)
            self.assertNotIn("changed:8080", content)

    def test_preview_rebuilds_site_files_without_writing(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            manager = TestManager(config, database, AuditLog(database))
            route = self.route()
            manager.routes.save(route)
            changed = ManagedRoute.from_json(route.to_json())
            changed.host = "new.example.com"
            rendered, diff = manager.preview(proposed=changed)
            self.assertIn("new.example.com {", rendered)
            self.assertNotIn("app.example.com {", rendered)
            self.assertIn("site-", diff)
            self.assertEqual(list(config.routes_dir.glob("*.caddy")), [])

    def test_conflicting_catch_all_routes_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            manager = TestManager(config, database, AuditLog(database))
            first_route = self.route()
            first_route.paths = []
            second_route = self.route()
            second_route.id = "second"
            second_route.name = "app-two"
            second_route.host = first_route.effective_host
            second_route.paths = []
            manager.routes.save(first_route)
            with self.assertRaisesRegex(ValueError, "multiple catch-all"):
                manager.preview(proposed=second_route)

    def test_failed_revision_restore_keeps_current_database_and_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            manager = TestManager(config, database, AuditLog(database))
            route = self.route()
            first_revision = manager.apply(Actor(username="admin"), "create", proposed=route)
            changed = ManagedRoute.from_json(route.to_json())
            changed.upstreams = [Upstream("current:8080")]
            manager.apply(Actor(username="admin"), "change", proposed=changed)
            manager.validation_error = "restore rejected"
            with self.assertRaises(RuntimeError):
                manager.restore_revision(Actor(username="admin"), first_revision)
            self.assertEqual(manager.routes.get(route.id).upstreams[0].address, "current:8080")
            self.assertIn("current:8080", next(config.routes_dir.glob("site-*.caddy")).read_text(encoding="utf-8"))

    def test_legacy_layout_migrates_routes_and_root_together(self) -> None:
        legacy_caddyfile = '''{
    admin 0.0.0.0:2019
}
{$DOMAIN}, *.{$DOMAIN} {
    import /etc/caddy/routes/*.caddy
    handle { respond "Service not configured" 404 }
}
'''
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            config.caddyfile_path.write_text(legacy_caddyfile, encoding="utf-8")
            database = Database(config)
            database.initialize()
            manager = TestManager(config, database, AuditLog(database))
            route = self.route()
            manager.routes.save(route)
            config.routes_dir.mkdir(parents=True, exist_ok=True)
            (config.routes_dir / "legacy.caddy").write_text("# managed-by caddy-ui\nold matcher\n", encoding="utf-8")
            self.assertTrue(manager.migrate_legacy_layout())
            self.assertEqual(config.caddyfile_path.read_text(encoding="utf-8"), DEFAULT_CADDYFILE)
            self.assertEqual(config.caddyfile_path.with_name("Caddyfile.pre-1.0").read_text(encoding="utf-8"), legacy_caddyfile)
            self.assertFalse((config.routes_dir / "legacy.caddy").exists())
            self.assertIn("app.example.com {", next(config.routes_dir.glob("site-*.caddy")).read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
