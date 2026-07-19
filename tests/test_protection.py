from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from caddy_ui.enhanced_web import Application
from tests.helpers import settings


class ProtectionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.settings = settings(Path(self.temporary.name))
        self.app = Application(self.settings)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_login_failures_persist_and_temporarily_restrict(self) -> None:
        for _ in range(9):
            state = self.app.security.record_login_failure("ui", "198.51.100.20", "admin")
            self.assertTrue(state["allowed"])
        state = self.app.security.record_login_failure("ui", "198.51.100.20", "admin")
        self.assertFalse(state["allowed"])
        self.assertGreaterEqual(state["retry_after"], 1)

        self.app.security.clear_login("ui", "198.51.100.20", "admin")
        self.assertTrue(self.app.security.login_state("ui", "198.51.100.20", "admin")["allowed"])

    def test_forwarded_headers_are_ignored_without_trusted_proxy(self) -> None:
        self.assertEqual(
            self.app.security.client_ip("10.20.30.40", {"X-Forwarded-For": "203.0.113.99"}),
            "10.20.30.40",
        )

        self.app.database.set_setting(
            "protection",
            {
                "level": "balanced",
                "trusted_proxies": ["10.20.30.40/32"],
                "allowlist": [],
            },
        )
        self.assertEqual(
            self.app.security.client_ip("10.20.30.40", {"X-Forwarded-For": "203.0.113.99"}),
            "203.0.113.99",
        )

    def test_manual_temporary_block_updates_shared_blocklist(self) -> None:
        self.app.security.ban_ip("203.0.113.25", "test", source="manual", seconds=900)
        rows = self.app.security.active_bans()
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["ip"], "203.0.113.25")
        content = self.app.security.blocklist_path.read_text(encoding="utf-8")
        self.assertIn("203.0.113.25|", content)
        self.assertIn("|test", content)

        self.app.security.unban_ip("203.0.113.25")
        self.assertEqual(self.app.security.active_bans(), [])


if __name__ == "__main__":
    unittest.main()
