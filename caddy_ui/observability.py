from __future__ import annotations

import logging
import threading
import time

from .analytics import AnalyticsRepository
from .config import Settings
from .protection import SecurityService


class ObservabilityJobRunner:
    """Runs low-cost analytics ingestion, threat detection, and retention jobs."""

    def __init__(self, settings: Settings, analytics: AnalyticsRepository, security: SecurityService):
        self.settings = settings
        self.analytics = analytics
        self.security = security
        self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self._run, name="caddy-ui-observability", daemon=True)

    def start(self) -> None:
        self.thread.start()

    def stop(self) -> None:
        self.stop_event.set()
        self.thread.join(timeout=5)

    def _run(self) -> None:
        next_ingest = 0.0
        next_threat_scan = 0.0
        next_compaction = 0.0
        next_blocklist = 0.0
        while not self.stop_event.wait(2):
            now = time.time()
            if now >= next_ingest:
                self._ingest()
                next_ingest = now + 15
            if now >= next_threat_scan:
                self._scan_threats()
                next_threat_scan = now + 60
            if now >= next_compaction:
                self._compact()
                next_compaction = now + 3600
            if now >= next_blocklist:
                self._sync_blocklist()
                next_blocklist = now + 30

    def _ingest(self) -> None:
        try:
            self.analytics.ingest(self.settings.access_log_path)
            self.security.ingest_guard_log(self.settings.access_log_path.with_name("security.log"))
        except Exception:
            logging.exception("Observability ingestion failed")

    def _scan_threats(self) -> None:
        try:
            self.security.scan_threats()
        except Exception:
            logging.exception("Threat detection failed")

    def _compact(self) -> None:
        try:
            self.analytics.compact()
        except Exception:
            logging.exception("Analytics retention failed")

    def _sync_blocklist(self) -> None:
        try:
            self.security.active_bans()
        except Exception:
            logging.exception("Security blocklist sync failed")
