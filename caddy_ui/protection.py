from __future__ import annotations

import ipaddress
import json
import logging
import os
import re
import sqlite3
import tempfile
import time
import urllib.parse
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any, Mapping

from .audit import Actor, AuditLog
from .caddy import CaddyManager
from .db import Database, utc_now
from .domain import ManagedRoute
from .monitoring import tail_lines
from .notifications import NotificationService


FEATURE_SCHEMA_VERSION = 1
PROTECTION_LEVELS = {"off", "balanced", "strict", "custom"}
SECURITY_SCHEMA = (
    """
    CREATE TABLE IF NOT EXISTS security_events (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        source_hash TEXT UNIQUE,
        occurred_at TEXT NOT NULL,
        kind TEXT NOT NULL,
        severity TEXT NOT NULL,
        client_ip TEXT NOT NULL,
        host TEXT NOT NULL DEFAULT '',
        endpoint TEXT NOT NULL DEFAULT '',
        reason TEXT NOT NULL,
        details_json TEXT NOT NULL DEFAULT '{}',
        resolved_at TEXT
    )
    """,
    "CREATE INDEX IF NOT EXISTS ix_security_events_time ON security_events(occurred_at DESC)",
    "CREATE INDEX IF NOT EXISTS ix_security_events_ip_time ON security_events(client_ip,occurred_at DESC)",
    """
    CREATE TABLE IF NOT EXISTS security_bans (
        ip TEXT PRIMARY KEY,
        created_at TEXT NOT NULL,
        expires_at TEXT NOT NULL,
        reason TEXT NOT NULL,
        source TEXT NOT NULL,
        updated_at TEXT NOT NULL
    )
    """,
    "CREATE INDEX IF NOT EXISTS ix_security_bans_expiry ON security_bans(expires_at)",
    """
    CREATE TABLE IF NOT EXISTS login_protection (
        scope_key TEXT PRIMARY KEY,
        failures INTEGER NOT NULL,
        first_failure_at TEXT NOT NULL,
        last_failure_at TEXT NOT NULL,
        blocked_until TEXT,
        escalation INTEGER NOT NULL DEFAULT 0
    )
    """,
)


def protection_settings(database: Database) -> dict[str, Any]:
    raw = database.setting("protection", {}) or {}
    level = str(raw.get("level", "balanced")).lower()
    if level not in PROTECTION_LEVELS:
        level = "balanced"
    defaults = {
        "balanced": {"requests": 300, "window_seconds": 60, "burst": 60, "block_seconds": 900},
        "strict": {"requests": 120, "window_seconds": 60, "burst": 20, "block_seconds": 900},
        "custom": {"requests": 300, "window_seconds": 60, "burst": 60, "block_seconds": 900},
        "off": {"requests": 0, "window_seconds": 60, "burst": 0, "block_seconds": 0},
    }[level]
    global_raw = raw.get("global", {}) if isinstance(raw.get("global"), dict) else {}
    login_raw = raw.get("login", {}) if isinstance(raw.get("login"), dict) else {}
    return {
        "level": level,
        "global": {
            "requests": max(1, int(global_raw.get("requests", defaults["requests"]) or defaults["requests"])) if level != "off" else 0,
            "window_seconds": max(1, int(global_raw.get("window_seconds", defaults["window_seconds"]) or defaults["window_seconds"])),
            "burst": max(0, int(global_raw.get("burst", defaults["burst"]) or 0)),
            "block_seconds": max(60, int(global_raw.get("block_seconds", defaults["block_seconds"]) or defaults["block_seconds"])) if level != "off" else 0,
        },
        "login": {
            "delay_after": max(1, int(login_raw.get("delay_after", 5) or 5)),
            "block_after": max(2, int(login_raw.get("block_after", 10) or 10)),
            "window_seconds": max(60, int(login_raw.get("window_seconds", 900) or 900)),
        },
        "trusted_proxies": [str(item).strip() for item in raw.get("trusted_proxies", []) if str(item).strip()],
        "allowlist": [str(item).strip() for item in raw.get("allowlist", []) if str(item).strip()],
        "route_overrides": raw.get("route_overrides", {}) if isinstance(raw.get("route_overrides"), dict) else {},
    }


def _parse_networks(values: list[str]) -> list[ipaddress._BaseNetwork]:
    networks: list[ipaddress._BaseNetwork] = []
    for value in values:
        try:
            networks.append(ipaddress.ip_network(value, strict=False))
        except ValueError:
            try:
                address = ipaddress.ip_address(value)
                networks.append(ipaddress.ip_network(f"{address}/{address.max_prefixlen}", strict=False))
            except ValueError:
                continue
    return networks


def _in_networks(value: str, networks: list[ipaddress._BaseNetwork]) -> bool:
    try:
        address = ipaddress.ip_address(value)
    except ValueError:
        return False
    return any(address in network for network in networks if address.version == network.version)


def _safe_route_identifier(value: str) -> str:
    return "".join(character if character.isalnum() or character == "_" else "_" for character in value)


def _duration(value: int) -> str:
    if value % 3600 == 0:
        return f"{value // 3600}h"
    if value % 60 == 0:
        return f"{value // 60}m"
    return f"{value}s"


class SecurityService:
    def __init__(self, database: Database, caddyfile_path: Path, notifications: NotificationService | None = None):
        self.database = database
        self.caddyfile_path = caddyfile_path
        self.notifications = notifications
        self.blocklist_path = caddyfile_path.parent / "security-blocklist.txt"
        self.security_log_path = Path("/var/log/caddy/security.log")
        self.ensure_schema()
        self.sync_blocklist()

    def ensure_schema(self) -> None:
        current = int(self.database.setting("feature_schema_security", 0) or 0)
        if current >= FEATURE_SCHEMA_VERSION:
            return
        self.database.backup("pre-security-migration")
        with self.database.transaction() as connection:
            for statement in SECURITY_SCHEMA:
                connection.execute(statement)
            result = connection.execute("PRAGMA integrity_check").fetchone()[0]
            if result != "ok":
                raise RuntimeError(f"Security migration integrity check failed: {result}")
        self.database.set_setting("feature_schema_security", FEATURE_SCHEMA_VERSION)

    def client_ip(self, peer_ip: str, headers: Mapping[str, str]) -> str:
        settings = protection_settings(self.database)
        trusted = _parse_networks(settings["trusted_proxies"])
        if not trusted or not _in_networks(peer_ip, trusted):
            return peer_ip
        forwarded = str(headers.get("X-Forwarded-For", ""))
        chain = [item.strip() for item in forwarded.split(",") if item.strip()]
        for candidate in reversed(chain):
            if not _in_networks(candidate, trusted):
                try:
                    return str(ipaddress.ip_address(candidate))
                except ValueError:
                    break
        real_ip = str(headers.get("X-Real-IP", "")).strip()
        try:
            return str(ipaddress.ip_address(real_ip)) if real_ip else peer_ip
        except ValueError:
            return peer_ip

    def login_state(self, scope: str, client_ip: str, username: str) -> dict[str, Any]:
        key = self._login_key(scope, client_ip, username)
        now = datetime.now(UTC)
        settings = protection_settings(self.database)["login"]
        with self.database.connect() as connection:
            row = connection.execute("SELECT * FROM login_protection WHERE scope_key=?", (key,)).fetchone()
        if not row:
            return {"allowed": True, "delay": 0.0, "retry_after": 0, "failures": 0}
        blocked_until = datetime.fromisoformat(row["blocked_until"]) if row["blocked_until"] else None
        if blocked_until and blocked_until > now:
            return {
                "allowed": False,
                "delay": 0.0,
                "retry_after": max(1, int((blocked_until - now).total_seconds())),
                "failures": int(row["failures"]),
            }
        last_failure = datetime.fromisoformat(row["last_failure_at"])
        if last_failure < now - timedelta(seconds=settings["window_seconds"]):
            with self.database.transaction() as connection:
                connection.execute("DELETE FROM login_protection WHERE scope_key=?", (key,))
            return {"allowed": True, "delay": 0.0, "retry_after": 0, "failures": 0}
        failures = int(row["failures"])
        delay = max(0.0, min(2.0, (failures - settings["delay_after"] + 1) * 0.35)) if failures >= settings["delay_after"] else 0.0
        return {"allowed": True, "delay": delay, "retry_after": 0, "failures": failures}

    def record_login_failure(self, scope: str, client_ip: str, username: str, host: str = "") -> dict[str, Any]:
        key = self._login_key(scope, client_ip, username)
        now = datetime.now(UTC)
        settings = protection_settings(self.database)["login"]
        with self.database.transaction() as connection:
            row = connection.execute("SELECT * FROM login_protection WHERE scope_key=?", (key,)).fetchone()
            if row and datetime.fromisoformat(row["last_failure_at"]) >= now - timedelta(seconds=settings["window_seconds"]):
                failures = int(row["failures"]) + 1
                escalation = int(row["escalation"])
                first_failure = row["first_failure_at"]
            else:
                failures = 1
                escalation = int(row["escalation"]) if row else 0
                first_failure = now.isoformat()
            blocked_until = None
            if failures >= settings["block_after"]:
                durations = (900, 3600, 86400)
                block_seconds = durations[min(escalation, len(durations) - 1)]
                blocked_until = (now + timedelta(seconds=block_seconds)).isoformat()
                escalation = min(escalation + 1, len(durations) - 1)
            connection.execute(
                """INSERT INTO login_protection(scope_key,failures,first_failure_at,last_failure_at,blocked_until,escalation)
                   VALUES(?,?,?,?,?,?) ON CONFLICT(scope_key) DO UPDATE SET failures=excluded.failures,
                   first_failure_at=excluded.first_failure_at,last_failure_at=excluded.last_failure_at,
                   blocked_until=excluded.blocked_until,escalation=excluded.escalation""",
                (key, failures, first_failure, now.isoformat(), blocked_until, escalation),
            )
        if blocked_until:
            seconds = int((datetime.fromisoformat(blocked_until) - now).total_seconds())
            self.record_event(
                "brute_force",
                "warning",
                client_ip,
                host,
                "/login" if scope == "ui" else "/__caddy_ui_auth/login",
                f"Login temporarily blocked after {failures} failed attempts.",
                {"scope": scope, "username": username, "block_seconds": seconds},
            )
        return self.login_state(scope, client_ip, username)

    def clear_login(self, scope: str, client_ip: str, username: str) -> None:
        with self.database.transaction() as connection:
            connection.execute("DELETE FROM login_protection WHERE scope_key=?", (self._login_key(scope, client_ip, username),))

    @staticmethod
    def _login_key(scope: str, client_ip: str, username: str) -> str:
        return f"{scope}:{client_ip}:{username.strip().lower()}"

    def record_event(
        self,
        kind: str,
        severity: str,
        client_ip: str,
        host: str,
        endpoint: str,
        reason: str,
        details: dict[str, Any] | None = None,
        source_hash: str | None = None,
    ) -> int | None:
        try:
            with self.database.transaction() as connection:
                cursor = connection.execute(
                    """INSERT OR IGNORE INTO security_events(source_hash,occurred_at,kind,severity,client_ip,host,endpoint,reason,details_json)
                       VALUES(?,?,?,?,?,?,?,?,?)""",
                    (source_hash, utc_now(), kind, severity, client_ip, host, endpoint, reason, json.dumps(details or {}, separators=(",", ":"), sort_keys=True)),
                )
                if cursor.rowcount != 1:
                    return None
                event_id = int(cursor.lastrowid)
        except sqlite3.IntegrityError:
            return None
        return event_id

    def ingest_guard_log(self, path: Path | None = None) -> int:
        path = path or self.security_log_path
        inserted = 0
        for line in tail_lines(path, max_bytes=8 * 1024 * 1024, limit=10_000):
            try:
                item = json.loads(line)
            except json.JSONDecodeError:
                continue
            source_hash = __import__("hashlib").sha256(line.encode("utf-8", errors="replace")).hexdigest()
            event_id = self.record_event(
                str(item.get("kind", "rate_limit")),
                str(item.get("severity", "info")),
                str(item.get("client_ip", "")),
                str(item.get("host", "")),
                str(item.get("endpoint", "")),
                str(item.get("reason", "Request blocked by Caddy protection.")),
                {key: value for key, value in item.items() if key not in {"kind", "severity", "client_ip", "host", "endpoint", "reason"}},
                source_hash,
            )
            if event_id is not None:
                inserted += 1
        return inserted

    def scan_threats(self) -> int:
        settings = protection_settings(self.database)
        if settings["level"] == "off":
            return 0
        now = datetime.now(UTC)
        since = (now - timedelta(minutes=5)).isoformat()
        global_limit = max(1, int(settings["global"]["requests"]))
        multiplier = 1.0 if settings["level"] == "strict" else 1.5
        with self.database.connect() as connection:
            rows = connection.execute(
                """SELECT remote_ip,MAX(client_type) client_type,COUNT(*) total,
                          SUM(CASE WHEN status=404 THEN 1 ELSE 0 END) not_found,
                          SUM(CASE WHEN status IN (401,403) THEN 1 ELSE 0 END) auth_denied,
                          COUNT(DISTINCT endpoint) distinct_endpoints
                   FROM request_events WHERE occurred_at>=? AND remote_ip<>'' AND client_type<>'internal'
                   GROUP BY remote_ip""",
                (since,),
            ).fetchall()
        detected = 0
        for row in rows:
            ip = str(row["remote_ip"])
            reasons: list[str] = []
            if int(row["total"]) >= int(global_limit * 5 * multiplier):
                reasons.append(f"{row['total']} requests in 5 minutes")
            if int(row["not_found"] or 0) >= int(40 * multiplier) and int(row["distinct_endpoints"] or 0) >= int(25 * multiplier):
                reasons.append(f"{row['not_found']} not-found responses across {row['distinct_endpoints']} endpoints")
            if int(row["auth_denied"] or 0) >= int(25 * multiplier):
                reasons.append(f"{row['auth_denied']} authorization failures")
            if not reasons or self._trusted_or_private(ip):
                continue
            if self._recent_event(ip, "threat_detected", minutes=10):
                continue
            reason = "; ".join(reasons)
            self.record_event("threat_detected", "warning", ip, "", "", reason, dict(row))
            self.ban_ip(ip, reason, source="automatic", progressive=True)
            detected += 1
        return detected

    def _recent_event(self, ip: str, kind: str, minutes: int) -> bool:
        since = (datetime.now(UTC) - timedelta(minutes=minutes)).isoformat()
        with self.database.connect() as connection:
            row = connection.execute(
                "SELECT 1 FROM security_events WHERE client_ip=? AND kind=? AND occurred_at>=? LIMIT 1",
                (ip, kind, since),
            ).fetchone()
        return bool(row)

    def _trusted_or_private(self, ip: str) -> bool:
        try:
            address = ipaddress.ip_address(ip)
        except ValueError:
            return True
        if address.is_private or address.is_loopback or address.is_link_local:
            return True
        settings = protection_settings(self.database)
        return _in_networks(ip, _parse_networks(settings["allowlist"]))

    def ban_ip(self, ip: str, reason: str, source: str = "manual", seconds: int | None = None, progressive: bool = False) -> None:
        address = str(ipaddress.ip_address(ip))
        if source == "automatic" and self._trusted_or_private(address):
            return
        now = datetime.now(UTC)
        if seconds is None:
            seconds = 86400 if source == "manual" else 900
        if progressive:
            since = (now - timedelta(days=7)).isoformat()
            with self.database.connect() as connection:
                count = int(connection.execute(
                    "SELECT COUNT(*) FROM security_events WHERE client_ip=? AND kind='auto_block' AND occurred_at>=?",
                    (address, since),
                ).fetchone()[0])
            seconds = (900, 3600, 86400)[min(count, 2)]
        expires = now + timedelta(seconds=max(60, min(seconds, 86400 if source == "automatic" else 31_536_000)))
        with self.database.transaction() as connection:
            connection.execute(
                """INSERT INTO security_bans(ip,created_at,expires_at,reason,source,updated_at) VALUES(?,?,?,?,?,?)
                   ON CONFLICT(ip) DO UPDATE SET expires_at=excluded.expires_at,reason=excluded.reason,source=excluded.source,updated_at=excluded.updated_at""",
                (address, now.isoformat(), expires.isoformat(), reason[:500], source, now.isoformat()),
            )
        kind = "auto_block" if source == "automatic" else "manual_block"
        self.record_event(kind, "warning" if source == "automatic" else "info", address, "", "", reason, {"expires_at": expires.isoformat()})
        self.sync_blocklist()
        if source == "automatic" and self.notifications and not self._recent_notification(address):
            self.notifications.create("warning", "security.threat", "Suspicious client blocked", f"{address}: {reason}", "ip", address)

    def _recent_notification(self, ip: str) -> bool:
        since = (datetime.now(UTC) - timedelta(minutes=15)).isoformat()
        with self.database.connect() as connection:
            row = connection.execute(
                "SELECT 1 FROM notifications WHERE event_type='security.threat' AND object_id=? AND created_at>=? LIMIT 1",
                (ip, since),
            ).fetchone()
        return bool(row)

    def unban_ip(self, ip: str) -> None:
        with self.database.transaction() as connection:
            connection.execute("DELETE FROM security_bans WHERE ip=?", (ip,))
        self.record_event("unblock", "info", ip, "", "", "IP block removed by administrator.")
        self.sync_blocklist()

    def active_bans(self) -> list[sqlite3.Row]:
        now = utc_now()
        with self.database.transaction() as connection:
            connection.execute("DELETE FROM security_bans WHERE expires_at<=?", (now,))
            rows = connection.execute("SELECT * FROM security_bans ORDER BY expires_at DESC").fetchall()
        self.sync_blocklist(rows)
        return rows

    def events(self, limit: int = 500, client_ip: str = "", kind: str = "") -> list[sqlite3.Row]:
        clauses = ["1=1"]
        args: list[Any] = []
        if client_ip:
            clauses.append("client_ip=?")
            args.append(client_ip)
        if kind:
            clauses.append("kind=?")
            args.append(kind)
        with self.database.connect() as connection:
            return connection.execute(
                f"SELECT * FROM security_events WHERE {' AND '.join(clauses)} ORDER BY occurred_at DESC,id DESC LIMIT ?",
                (*args, min(max(limit, 1), 5000)),
            ).fetchall()

    def summary(self, hours: int = 24) -> dict[str, Any]:
        since = (datetime.now(UTC) - timedelta(hours=max(1, hours))).isoformat()
        with self.database.connect() as connection:
            row = connection.execute(
                """SELECT COUNT(*) events,
                          SUM(CASE WHEN kind IN ('rate_limit','auto_block','manual_block','blocked') THEN 1 ELSE 0 END) blocked,
                          SUM(CASE WHEN kind='brute_force' THEN 1 ELSE 0 END) brute_force,
                          COUNT(DISTINCT CASE WHEN client_ip<>'' THEN client_ip END) clients
                   FROM security_events WHERE occurred_at>=?""",
                (since,),
            ).fetchone()
            top = connection.execute(
                """SELECT client_ip,COUNT(*) events FROM security_events
                   WHERE occurred_at>=? AND client_ip<>'' GROUP BY client_ip ORDER BY events DESC LIMIT 10""",
                (since,),
            ).fetchall()
        return {**dict(row), "top_ips": [(item["client_ip"], item["events"]) for item in top], "active_bans": len(self.active_bans())}

    def sync_blocklist(self, rows: list[sqlite3.Row] | None = None) -> None:
        if rows is None:
            now = utc_now()
            with self.database.connect() as connection:
                rows = connection.execute("SELECT * FROM security_bans WHERE expires_at>? ORDER BY ip", (now,)).fetchall()
        self.blocklist_path.parent.mkdir(parents=True, exist_ok=True)
        lines = [f"{row['ip']}|{row['expires_at']}|{str(row['reason']).replace(chr(10), ' ')[:300]}" for row in rows]
        temporary = self.blocklist_path.with_suffix(".tmp")
        temporary.write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8", newline="\n")
        temporary.replace(self.blocklist_path)


class SecurityCaddyManager(CaddyManager):
    def _rendered_for(self, routes: list[ManagedRoute]) -> dict[str, str]:
        content = super()._rendered_for(routes)
        settings = protection_settings(self.database)
        if settings["level"] == "off":
            return content
        for route in routes:
            if not route.enabled:
                continue
            route_settings = self._route_settings(route.id, settings)
            if route_settings is None:
                continue
            matcher = f"caddy_ui_{_safe_route_identifier(route.id)}"
            needle = f"    handle @{matcher} {{\n"
            directive = self._guard_directive(route_settings, settings)
            for filename, value in list(content.items()):
                if needle in value:
                    content[filename] = value.replace(needle, needle + directive, 1)
                    break
        return content

    @staticmethod
    def _route_settings(route_id: str, settings: dict[str, Any]) -> dict[str, int] | None:
        base = dict(settings["global"])
        override = settings["route_overrides"].get(route_id, {}) if isinstance(settings["route_overrides"], dict) else {}
        mode = str(override.get("mode", "inherit")) if isinstance(override, dict) else "inherit"
        if mode == "off":
            return None
        if mode == "custom":
            for key in ("requests", "window_seconds", "burst", "block_seconds"):
                if key in override:
                    minimum = 0 if key == "burst" else 1
                    base[key] = max(minimum, int(override[key]))
        return base

    def _guard_directive(self, values: dict[str, int], settings: dict[str, Any]) -> str:
        lines = [
            "        caddy_ui_guard {",
            f"            requests {values['requests']}",
            f"            window {_duration(values['window_seconds'])}",
            f"            burst {values['burst']}",
            f"            block {_duration(values['block_seconds'])}",
            "            blocklist_file /etc/caddy/security-blocklist.txt",
            "            event_log /var/log/caddy/security.log",
        ]
        for value in settings["trusted_proxies"]:
            lines.append(f"            trusted_proxy {value}")
        for value in settings["allowlist"]:
            lines.append(f"            allowlist {value}")
        lines.extend(["        }", ""])
        return "\n".join(lines)

    def apply_security_configuration(self, actor: Actor | None = None) -> str:
        actor = actor or Actor(username="system", remote_address="local")
        with self._lock:
            self.database.backup("pre-security-config")
            with tempfile.TemporaryDirectory(prefix="caddy-ui-security-") as temporary_name:
                backup = Path(temporary_name) / "routes"
                if self.settings.routes_dir.exists():
                    __import__("shutil").copytree(self.settings.routes_dir, backup)
                revision_id = ""
                try:
                    content = self.rendered()
                    revision_id = self._create_revision(actor, "Apply security protection", content)
                    self._write_managed_files(content)
                    self.validate()
                    if self.settings.auto_reload:
                        self.reload()
                    self._mark_revision_applied(revision_id)
                    self.audit.record(actor, "security.apply", "system", "protection", revision_id=revision_id)
                    return revision_id
                except Exception as exc:
                    self._restore_directory(backup)
                    try:
                        if self.settings.auto_reload:
                            self.reload()
                    except Exception:
                        logging.exception("Caddy reload after security rollback failed")
                    self.audit.record(actor, "security.apply", "system", "protection", result=f"failed: {exc}", revision_id=revision_id)
                    raise
