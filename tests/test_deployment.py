from __future__ import annotations

import unittest
from pathlib import Path


ROOT = Path(__file__).parents[1]


class DeploymentTests(unittest.TestCase):
    def test_legacy_compose_remains_available_for_rollback(self) -> None:
        value = (ROOT / "compose.yml").read_text(encoding="utf-8")
        service_lines = [
            line
            for line in value.splitlines()
            if line.startswith("  ") and not line.startswith("    ") and line.endswith(":")
        ]
        service_names = service_lines[: service_lines.index("  etc:") if "  etc:" in service_lines else len(service_lines)]
        self.assertEqual(service_names, ["  caddy:", "  caddy-ui:"])
        self.assertNotIn("docker.sock", value)

    def test_production_template_uses_dotnet_postgresql_and_migration_gates(self) -> None:
        value = (ROOT / "deploy" / "docker-compose.yml").read_text(encoding="utf-8")
        for service in ("postgres", "migrate", "legacy-import", "config-init", "caddy", "caddy-ui"):
            with self.subTest(service=service):
                self.assertIn(f"  {service}:\n", value)
        self.assertIn("postgres:17-alpine", value)
        self.assertIn('command: ["migrate", "schema"]', value)
        self.assertIn("--source /legacy/caddy-ui.db", value)
        self.assertIn('Routing__WriteMode: active', value)
        self.assertIn('Operations__WorkerEnabled: "true"', value)
        self.assertIn('Operations__DnsWriteMode: active', value)
        self.assertIn('IpSecurity__BlockWriteMode: active', value)
        self.assertIn("/usr/local/bin/caddy-remote", value)
        self.assertNotIn("docker.sock", value)

    def test_admin_port_is_reachable_and_portal_is_internal(self) -> None:
        value = (ROOT / "deploy" / "docker-compose.yml").read_text(encoding="utf-8")
        self.assertIn('"${CADDY_UI_BIND_ADDRESS:-0.0.0.0}:8098:8098"', value)
        self.assertIn('      - "8099"', value)
        self.assertNotIn('      - "8099:8099"', value)
        self.assertIn("no-new-privileges:true", value)
        self.assertIn("cap_drop:", value)

    def test_release_template_is_version_pinned(self) -> None:
        value = (ROOT / "deploy" / "docker-compose.yml").read_text(encoding="utf-8")
        self.assertEqual(value.count("ghcr.io/juloc/caddy-ui:__CADDY_UI_VERSION__"), 1)
        self.assertNotIn(":latest", value)

    def test_legacy_state_is_mounted_read_only_and_preserved(self) -> None:
        value = (ROOT / "deploy" / "docker-compose.yml").read_text(encoding="utf-8")
        self.assertGreaterEqual(value.count("ui-data:/legacy:ro"), 2)
        self.assertIn("ui-state:/state", value)
        self.assertIn("postgres-data:/var/lib/postgresql/data", value)


if __name__ == "__main__":
    unittest.main()
