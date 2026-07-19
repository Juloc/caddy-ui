from __future__ import annotations

import ipaddress
import json
import logging
import threading
import time
import urllib.request
from datetime import UTC, datetime, timedelta

from .config import Settings
from .db import Database
from .notifications import NotificationService
from .monitoring import caddy_status, certificate_files, parse_access_logs, route_health
from .providers.netcup import NetcupProvider
from .repositories import ProviderRepository, RouteRepository


class JobRunner:
    def __init__(self, settings: Settings, database: Database, notifications: NotificationService):
        self.settings = settings
        self.database = database
        self.notifications = notifications
        self.stop_event = threading.Event()
        self.thread = threading.Thread(target=self._run, name="caddy-ui-jobs", daemon=True)

    def start(self) -> None:
        self.thread.start()

    def stop(self) -> None:
        self.stop_event.set()
        self.thread.join(timeout=5)

    def _run(self) -> None:
        next_backup = 0.0
        next_ddns = 0.0
        next_traffic = 0.0
        next_monitor = 0.0
        while not self.stop_event.wait(5):
            now = time.time()
            if now >= next_backup:
                self._backup()
                next_backup = now + 86400
            if now >= next_ddns:
                interval = int(self.database.setting("ddns_interval_seconds", 300) or 300)
                self._ddns()
                next_ddns = now + max(60, interval)
            if now >= next_traffic:
                self._aggregate_traffic()
                next_traffic = now + 300
            if now >= next_monitor:
                self._monitor_health()
                next_monitor = now + 300

    def _backup(self) -> None:
        try:
            self.database.backup("daily")
            self._prune_backups()
        except Exception as exc:
            logging.exception("Daily backup failed")
            self.notifications.create("error", "backup.failed", "Backup failed", str(exc))

    def _prune_backups(self) -> None:
        files = sorted(self.settings.backup_dir.glob("caddy-ui-*-daily.db"), reverse=True)
        for path in files[30:]:
            path.unlink(missing_ok=True)

    def _ddns(self) -> None:
        config = self.database.setting("ddns", {}) or {}
        if not config.get("enabled"):
            return
        provider = ProviderRepository(self.database).get(str(config.get("provider_id", "")))
        if not provider:
            return
        try:
            request = urllib.request.Request(config.get("public_ip_url", "https://api64.ipify.org"), headers={"User-Agent": "caddy-ui/1.0"})
            with urllib.request.urlopen(request, timeout=20) as response:
                public_ip = str(ipaddress.ip_address(response.read(128).decode("utf-8").strip()))
            if ipaddress.ip_address(public_ip).version != 4:
                raise RuntimeError("DDNS currently expects an IPv4 address.")
            changed = NetcupProvider(provider).update_ddns(
                str(config.get("domain", "")),
                list(config.get("hosts", ["@", "*"])),
                public_ip,
            )
            self.database.set_setting("ddns_last_result", {"at": datetime.now(UTC).isoformat(), "ok": True, "changed": changed, "ip": public_ip})
        except Exception as exc:
            logging.exception("DDNS update failed")
            self.database.set_setting("ddns_last_result", {"at": datetime.now(UTC).isoformat(), "ok": False, "error": str(exc)})
            self.notifications.create("error", "ddns.failed", "DDNS update failed", str(exc))

    def _aggregate_traffic(self) -> None:
        try:
            last_timestamp = float(self.database.setting("traffic_last_timestamp", 0) or 0)
            entries = sorted(parse_access_logs(self.settings.access_log_path, 5000), key=lambda item: float(item.get("timestamp") or 0))
            newest = last_timestamp
            with self.database.transaction() as connection:
                for item in entries:
                    timestamp = float(item.get("timestamp") or 0)
                    if timestamp <= last_timestamp:
                        continue
                    instant = datetime.fromtimestamp(timestamp, UTC).replace(minute=0, second=0, microsecond=0)
                    status = int(item.get("status") or 0)
                    status_class = f"{status // 100}xx" if status else "unknown"
                    connection.execute(
                        """INSERT INTO traffic_buckets(bucket_start,granularity,host,status_class,requests,bytes_sent)
                           VALUES(?,?,?,?,1,?)
                           ON CONFLICT(bucket_start,granularity,host,status_class)
                           DO UPDATE SET requests=requests+1,bytes_sent=bytes_sent+excluded.bytes_sent""",
                        (instant.isoformat(), "hour", str(item.get("host") or "unknown"), status_class, int(item.get("size") or 0)),
                    )
                    newest = max(newest, timestamp)
            if newest > last_timestamp:
                self.database.set_setting("traffic_last_timestamp", newest)
            self._compact_traffic()
        except Exception:
            logging.exception("Traffic aggregation failed")

    def _compact_traffic(self) -> None:
        hour_cutoff = (datetime.now(UTC) - timedelta(days=30)).isoformat()
        day_cutoff = (datetime.now(UTC) - timedelta(days=365)).isoformat()
        with self.database.transaction() as connection:
            hourly = connection.execute(
                """SELECT substr(bucket_start,1,10) AS day,host,status_class,SUM(requests) requests,SUM(bytes_sent) bytes_sent
                   FROM traffic_buckets WHERE granularity='hour' AND bucket_start<?
                   GROUP BY day,host,status_class""",
                (hour_cutoff,),
            ).fetchall()
            for row in hourly:
                connection.execute(
                    """INSERT INTO traffic_buckets(bucket_start,granularity,host,status_class,requests,bytes_sent)
                       VALUES(?,?,?,?,?,?) ON CONFLICT(bucket_start,granularity,host,status_class)
                       DO UPDATE SET requests=requests+excluded.requests,bytes_sent=bytes_sent+excluded.bytes_sent""",
                    (f"{row['day']}T00:00:00+00:00", "day", row["host"], row["status_class"], row["requests"], row["bytes_sent"]),
                )
            connection.execute("DELETE FROM traffic_buckets WHERE granularity='hour' AND bucket_start<?", (hour_cutoff,))
            daily = connection.execute(
                """SELECT substr(bucket_start,1,7) AS month,host,status_class,SUM(requests) requests,SUM(bytes_sent) bytes_sent
                   FROM traffic_buckets WHERE granularity='day' AND bucket_start<?
                   GROUP BY month,host,status_class""",
                (day_cutoff,),
            ).fetchall()
            for row in daily:
                connection.execute(
                    """INSERT INTO traffic_buckets(bucket_start,granularity,host,status_class,requests,bytes_sent)
                       VALUES(?,?,?,?,?,?) ON CONFLICT(bucket_start,granularity,host,status_class)
                       DO UPDATE SET requests=requests+excluded.requests,bytes_sent=bytes_sent+excluded.bytes_sent""",
                    (f"{row['month']}-01T00:00:00+00:00", "month", row["host"], row["status_class"], row["requests"], row["bytes_sent"]),
                )
            connection.execute("DELETE FROM traffic_buckets WHERE granularity='day' AND bucket_start<?", (day_cutoff,))

    def _monitor_health(self) -> None:
        try:
            routes = RouteRepository(self.database).list()
            health = route_health(routes, self.settings)
            current: dict[str, bool] = {"caddy": bool(caddy_status(self.settings).get("admin"))}
            descriptions: dict[str, tuple[str, str, str]] = {
                "caddy": ("caddy.down", "Caddy unavailable", "The Caddy admin API is not reachable."),
            }
            for route in routes:
                if not route.enabled:
                    continue
                checks = (
                    ("public", "route.dns.down", "Public DNS unavailable"),
                    ("upstream", "route.upstream.down", "Upstream unavailable"),
                )
                for kind, event, title in checks:
                    key = f"route:{route.id}:{kind}"
                    result = health.get(route.id, {}).get(kind, {})
                    current[key] = bool(result.get("ok"))
                    descriptions[key] = (event, title, f"{route.effective_host}: {result.get('detail', 'health check failed')}")
            for certificate in certificate_files(self.settings.caddy_data_path):
                key = f"certificate:{certificate['name']}"
                current[key] = certificate["days"] >= 21
                descriptions[key] = ("certificate.expiring", "Certificate expiring", f"{certificate['name']} expires in {certificate['days']} days.")

            # Old releases performed an internal HTTPS self-check and could create false
            # route.public.down alerts because of hairpin NAT or split DNS. Retire them.
            self.notifications.acknowledge_event_type("route.public.down")

            previous = self.database.setting("monitor_state", {}) or {}
            for key, healthy in current.items():
                event, title, message = descriptions[key]
                object_type, _, object_id = key.partition(":")
                if healthy:
                    self.notifications.acknowledge_matching(event, object_type, object_id)
                elif previous.get(key) is not False:
                    self.notifications.create("error", event, title, message, object_type, object_id)
            self.database.set_setting("monitor_state", current)
        except Exception:
            logging.exception("Health monitoring failed")
