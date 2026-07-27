from __future__ import annotations

import json
import tempfile
import unittest
from datetime import UTC, datetime, timedelta
from pathlib import Path

from caddy_ui.analytics import AnalyticsFilters, normalize_endpoint, redact_uri
from caddy_ui.enhanced_web import Application
from tests.helpers import settings


class AnalyticsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.settings = settings(Path(self.temporary.name))
        self.app = Application(self.settings)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_ingest_redacts_deduplicates_and_builds_percentiles(self) -> None:
        now = datetime.now(UTC).timestamp()
        lines = [
            {
                "ts": now - 2,
                "request": {
                    "host": "api.example.com",
                    "method": "GET",
                    "uri": "/api/users/123?token=secret&view=full",
                    "remote_ip": "203.0.113.10",
                    "headers": {"User-Agent": ["Mozilla/5.0"]},
                },
                "status": 200,
                "size": 512,
                "duration": 0.12,
            },
            {
                "ts": now - 1,
                "request": {
                    "host": "api.example.com",
                    "method": "GET",
                    "uri": "/api/users/456?code=private",
                    "remote_ip": "203.0.113.10",
                    "headers": {"User-Agent": ["Mozilla/5.0"]},
                },
                "status": 503,
                "size": 64,
                "duration": 1.5,
            },
            {
                "ts": now,
                "request": {
                    "host": "www.example.com",
                    "method": "GET",
                    "uri": "/assets/app.js",
                    "remote_ip": "198.51.100.3",
                    "headers": {"User-Agent": ["Googlebot/2.1"]},
                },
                "status": 200,
                "size": 2048,
                "duration": 0.02,
            },
        ]
        self.settings.access_log_path.write_text("\n".join(json.dumps(item) for item in lines) + "\n", encoding="utf-8")

        self.assertEqual(self.app.analytics.ingest(self.settings.access_log_path), 3)
        self.assertEqual(self.app.analytics.ingest(self.settings.access_log_path), 0)

        start, end = self.app.analytics.resolve_range("1h")
        summary = self.app.analytics.summary(AnalyticsFilters(), start, end)
        self.assertEqual(summary["requests"], 3)
        self.assertEqual(summary["errors_5xx"], 1)
        self.assertGreaterEqual(summary["p95_ms"], 120)

        events = self.app.analytics.events(AnalyticsFilters(host="api.example.com"), start, end)
        self.assertEqual(len(events), 2)
        self.assertEqual(events[0]["endpoint"], "/api/users/{id}")
        self.assertIn("code=%5Bredacted%5D", events[0]["uri"])
        self.assertNotIn("private", events[0]["uri"])

        errors = self.app.analytics.events(AnalyticsFilters(status="errors"), start, end)
        self.assertEqual(len(errors), 1)
        self.assertEqual(errors[0]["status"], 503)

        bot = self.app.analytics.events(AnalyticsFilters(client_type="bot"), start, end)
        self.assertEqual(len(bot), 1)
        self.assertEqual(bot[0]["category"], "asset")

    def test_uri_redaction_and_endpoint_normalization_are_safe(self) -> None:
        self.assertEqual(normalize_endpoint("/orders/42/items/550e8400-e29b-41d4-a716-446655440000"), "/orders/{id}/items/{id}")
        value = redact_uri("/callback?access_token=abc&state=ok&password=hunter2")
        self.assertIn("access_token=%5Bredacted%5D", value)
        self.assertIn("password=%5Bredacted%5D", value)
        self.assertIn("state=ok", value)
        self.assertNotIn("hunter2", value)

    def test_saved_views_round_trip(self) -> None:
        with self.app.database.connect() as connection:
            user_id = connection.execute("SELECT id FROM users WHERE username='admin'").fetchone()[0]
        self.app.analytics.save_view(user_id, "logs", "Server errors", {"status": "5xx", "range": "24h"})
        rows = self.app.analytics.saved_views(user_id, "logs")
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["name"], "Server errors")

    def test_compaction_preserves_large_old_traffic_without_raw_client_data(self) -> None:
        old = datetime.now(UTC) - timedelta(days=31, hours=2)
        lines = []
        for index in range(5000):
            lines.append(
                json.dumps(
                    {
                        "ts": old.timestamp() + index / 10,
                        "request": {
                            "host": "archive.example.com",
                            "method": "GET",
                            "uri": f"/api/orders/{index}",
                            "remote_ip": f"203.0.113.{(index % 200) + 1}",
                            "headers": {"User-Agent": ["SyntheticLoad/1.0"]},
                        },
                        "status": 500 if index % 50 == 0 else 200,
                        "size": 256,
                        "duration": 0.05 + (index % 20) / 1000,
                    },
                    separators=(",", ":"),
                )
            )
        self.settings.access_log_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        self.assertEqual(self.app.analytics.ingest(self.settings.access_log_path), 5000)

        self.app.analytics.compact()

        with self.app.database.connect() as connection:
            raw_count = connection.execute("SELECT COUNT(*) FROM request_events WHERE host='archive.example.com'").fetchone()[0]
            daily_count = connection.execute(
                "SELECT COALESCE(SUM(requests),0) FROM analytics_buckets WHERE granularity='day' AND host='archive.example.com'"
            ).fetchone()[0]
        self.assertEqual(raw_count, 0)
        self.assertEqual(daily_count, 5000)

        start, end = self.app.analytics.resolve_range("1y")
        summary = self.app.analytics.summary(AnalyticsFilters(host="archive.example.com"), start, end)
        self.assertEqual(summary["requests"], 5000)
        self.assertEqual(summary["errors_5xx"], 100)
        self.assertEqual(self.app.analytics.events(AnalyticsFilters(host="archive.example.com"), start, end), [])


if __name__ == "__main__":
    unittest.main()
