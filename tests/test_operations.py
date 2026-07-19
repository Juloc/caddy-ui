from __future__ import annotations

import tempfile
import unittest
from datetime import UTC, datetime, timedelta
from pathlib import Path

from caddy_ui.db import Database
from caddy_ui.jobs import JobRunner
from caddy_ui.notifications import NotificationService
from tests.helpers import settings


class OperationsTests(unittest.TestCase):
    def test_traffic_retention_compacts_hourly_and_daily_buckets(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            now = datetime.now(UTC)
            old_hour = (now - timedelta(days=45)).replace(minute=0, second=0, microsecond=0).isoformat()
            old_day = (now - timedelta(days=400)).replace(hour=0, minute=0, second=0, microsecond=0).isoformat()
            with database.transaction() as connection:
                connection.execute("INSERT INTO traffic_buckets VALUES(?,?,?,?,?,?)", (old_hour, "hour", "app.example.com", "2xx", 5, 100))
                connection.execute("INSERT INTO traffic_buckets VALUES(?,?,?,?,?,?)", (old_day, "day", "app.example.com", "2xx", 7, 200))
            JobRunner(config, database, NotificationService(database))._compact_traffic()
            with database.connect() as connection:
                granularities = {row[0] for row in connection.execute("SELECT granularity FROM traffic_buckets")}
            self.assertNotIn("hour", granularities)
            self.assertIn("day", granularities)
            self.assertIn("month", granularities)

    def test_health_notifications_are_created_once_per_failure_transition(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            notifications = NotificationService(database)
            runner = JobRunner(config, database, notifications)
            runner._monitor_health()
            runner._monitor_health()
            rows = notifications.unacknowledged()
            self.assertEqual(len([row for row in rows if row["event_type"] == "caddy.down"]), 1)


if __name__ == "__main__":
    unittest.main()
