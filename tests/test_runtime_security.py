from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from caddy_ui.enhanced_web import Application
from caddy_ui.runtime_security import PRIVATE_ALLOWLISTS, RuntimeSecurityCaddyManager
from tests.helpers import settings


class RuntimeSecurityCompatibilityTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.settings = settings(Path(self.temporary.name))
        self.app = Application(self.settings)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_companion_rendering_omits_bundle_only_directive(self) -> None:
        manager = RuntimeSecurityCaddyManager(self.settings, self.app.database, self.app.audit)
        with patch("caddy_ui.runtime_security.bundled_guard_available", return_value=False):
            rendered = manager._rendered_for(self.app.routes.list())
        self.assertNotIn("caddy_ui_guard", "\n".join(rendered.values()))

    def test_private_networks_are_added_to_runtime_allowlist(self) -> None:
        manager = RuntimeSecurityCaddyManager(self.settings, self.app.database, self.app.audit)
        text = manager._guard_directive(
            {"requests": 300, "window_seconds": 60, "burst": 60, "block_seconds": 900},
            {"trusted_proxies": [], "allowlist": []},
        )
        for network in PRIVATE_ALLOWLISTS:
            self.assertIn(f"allowlist {network}", text)


if __name__ == "__main__":
    unittest.main()
