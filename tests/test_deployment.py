from __future__ import annotations

import unittest
from pathlib import Path


ROOT = Path(__file__).parents[1]


class DeploymentTests(unittest.TestCase):
    def test_compose_uses_exactly_two_services_without_docker_socket(self) -> None:
        value = (ROOT / "compose.yml").read_text(encoding="utf-8")
        service_lines = [line for line in value.splitlines() if line.startswith("  ") and not line.startswith("    ") and line.endswith(":")]
        service_names = service_lines[: service_lines.index("  etc:") if "  etc:" in service_lines else len(service_lines)]
        self.assertEqual(service_names, ["  caddy:", "  caddy-ui:"])
        self.assertNotIn("docker.sock", value)

    def test_release_template_is_version_pinned(self) -> None:
        value = (ROOT / "deploy" / "docker-compose.yml").read_text(encoding="utf-8")
        self.assertEqual(value.count("ghcr.io/juloc/caddy-ui:__CADDY_UI_VERSION__"), 2)
        self.assertNotIn(":latest", value)


if __name__ == "__main__":
    unittest.main()
