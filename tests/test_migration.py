from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from caddy_ui.audit import AuditLog
from caddy_ui.db import Database
from caddy_ui.migration import import_legacy
from caddy_ui.repositories import ProviderRepository, RouteRepository
from tests.helpers import settings


class MigrationTests(unittest.TestCase):
    def test_imports_legacy_provider_and_route_once(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            config.ensure_directories()
            config.legacy_config_path.write_text(
                json.dumps(
                    {
                        "settings": {"domain": "legacy.example"},
                        "providers": [
                            {
                                "id": "netcup-main",
                                "type": "netcup",
                                "label": "Netcup",
                                "domains": ["legacy.example"],
                                "customer_number": "{env.NETCUP_CUSTOMER_NUMBER}",
                                "api_key": "{env.NETCUP_API_KEY}",
                                "api_password": "{env.NETCUP_API_PASSWORD}",
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            metadata = {"name": "app", "host": "", "upstream": "app:8080", "tls_skip_verify": False}
            (config.routes_dir / "app.caddy").write_text(
                "# managed-by caddy-ui\n# caddy-ui-route: " + json.dumps(metadata) + "\n",
                encoding="utf-8",
            )
            database = Database(config)
            database.initialize()
            first = import_legacy(config, database, AuditLog(database))
            second = import_legacy(config, database, AuditLog(database))
            self.assertEqual(first, {"providers": 1, "routes": 1})
            self.assertEqual(second, {"providers": 0, "routes": 0})
            self.assertEqual(len(ProviderRepository(database).list()), 1)
            self.assertEqual(RouteRepository(database).list()[0].effective_host, "app.legacy.example")


if __name__ == "__main__":
    unittest.main()
