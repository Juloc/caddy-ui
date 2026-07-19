from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from caddy_ui.audit import Actor, AuditLog, redact
from caddy_ui.db import Database
from tests.helpers import settings


class AuditTests(unittest.TestCase):
    def test_secrets_are_redacted(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            database = Database(settings(Path(directory)))
            database.initialize()
            audit = AuditLog(database)
            audit.record(Actor(username="admin"), "provider.save", "provider", "main", after={"api_key": "secret", "nested": {"password": "secret"}, "label": "Main"})
            event = audit.list()[0]
            value = json.loads(event["after_json"])
            self.assertEqual(value["api_key"], "[redacted]")
            self.assertEqual(value["nested"]["password"], "[redacted]")
            self.assertEqual(value["label"], "Main")

    def test_custom_snippets_and_sensitive_headers_are_redacted(self) -> None:
        value = redact({"custom_snippet": "header Authorization bearer-secret", "request_headers": [{"name": "Authorization", "value": "bearer-secret"}]})
        self.assertEqual(value["custom_snippet"], "[redacted]")
        self.assertEqual(value["request_headers"][0]["value"], "[redacted]")


if __name__ == "__main__":
    unittest.main()
