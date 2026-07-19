from __future__ import annotations

import unittest
from pathlib import Path


class VersionVisibilityTests(unittest.TestCase):
    def test_client_displays_runtime_version_from_health_endpoint(self) -> None:
        script = Path("caddy_ui/static/app.js").read_text(encoding="utf-8")
        self.assertIn('fetch("/api/health"', script)
        self.assertIn("Caddy UI v${health.version}", script)

    def test_entrypoint_logs_runtime_version(self) -> None:
        entrypoint = Path("caddy_ui_entrypoint.py").read_text(encoding="utf-8")
        self.assertIn("Caddy UI v{__version__} starting", entrypoint)


if __name__ == "__main__":
    unittest.main()
