from __future__ import annotations

import csv
import hashlib
import io
import ipaddress
import json
import re
import sqlite3
import urllib.parse
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any, Iterable

from .db import Database, utc_now
from .monitoring import tail_lines


FEATURE_SCHEMA_VERSION = 1
RAW_RETENTION_DAYS = 30
AGGREGATE_RETENTION_DAYS = 365
SENSITIVE_QUERY_WORDS = (
    "token",
    "secret",
    "password",
    "passwd",
    "key",
    "code",
    "authorization",
    "auth",
    "session",
    "cookie",
    "signature",
)
ASSET_EXTENSIONS = {
    ".avif",
    ".css",
    ".eot",
    ".gif",
    ".ico",
    ".jpeg",
    ".jpg",
    ".js",
    ".json",
    ".map",
    ".mp3",
    ".mp4",
    ".ogg",
    ".pdf",
    ".png",
    ".svg",
    ".ttf",
    ".txt",
    ".webmanifest",
    ".webp",
    ".woff",
    ".woff2",
    ".xml",
}
BOT_RE = re.compile(
    r"(?:bot|crawler|spider|slurp|bingpreview|facebookexternalhit|whatsapp|telegrambot|discordbot|"
    r"uptimerobot|statuscake|pingdom|headlesschrome|python-requests|go-http-client|curl/|wget/)",
    re.IGNORECASE,
)
UUID_SEGMENT_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", re.IGNORECASE)
OPAQUE_ID_RE = re.compile(r"^(?:[0-9a-f]{16,}|[A-Za-z0-9_-]{24,})$")


ANALYTICS_SCHEMA = (
    """
    CREATE TABLE IF NOT EXISTS request_events (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        source_hash TEXT NOT NULL UNIQUE,
        occurred_at TEXT NOT NULL,
        occurred_ts REAL NOT NULL,
        host TEXT NOT NULL,
        method TEXT NOT NULL,
        uri TEXT NOT NULL,
        path TEXT NOT NULL,
        endpoint TEXT NOT NULL,
        status INTEGER NOT NULL,
        bytes_sent INTEGER NOT NULL,
        duration_ms REAL NOT NULL,
        remote_ip TEXT NOT NULL,
        user_agent TEXT NOT NULL,
        client_type TEXT NOT NULL CHECK (client_type IN ('human','bot','internal','unknown')),
        category TEXT NOT NULL CHECK (category IN ('page','api','asset','websocket','other'))
    )
    """,
    "CREATE INDEX IF NOT EXISTS ix_request_events_time ON request_events(occurred_at DESC)",
    "CREATE INDEX IF NOT EXISTS ix_request_events_host_time ON request_events(host,occurred_at DESC)",
    "CREATE INDEX IF NOT EXISTS ix_request_events_endpoint_time ON request_events(endpoint,occurred_at DESC)",
    "CREATE INDEX IF NOT EXISTS ix_request_events_ip_time ON request_events(remote_ip,occurred_at DESC)",
    "CREATE INDEX IF NOT EXISTS ix_request_events_status_time ON request_events(status,occurred_at DESC)",
    "CREATE INDEX IF NOT EXISTS ix_request_events_duration_time ON request_events(duration_ms,occurred_at DESC)",
    """
    CREATE TABLE IF NOT EXISTS analytics_buckets (
        bucket_start TEXT NOT NULL,
        granularity TEXT NOT NULL CHECK (granularity IN ('hour','day')),
        host TEXT NOT NULL,
        endpoint TEXT NOT NULL,
        method TEXT NOT NULL,
        status_class TEXT NOT NULL,
        client_type TEXT NOT NULL,
        category TEXT NOT NULL,
        requests INTEGER NOT NULL,
        bytes_sent INTEGER NOT NULL,
        duration_sum_ms REAL NOT NULL,
        duration_max_ms REAL NOT NULL,
        lt_100 INTEGER NOT NULL,
        lt_250 INTEGER NOT NULL,
        lt_500 INTEGER NOT NULL,
        lt_1000 INTEGER NOT NULL,
        lt_3000 INTEGER NOT NULL,
        lt_10000 INTEGER NOT NULL,
        ge_10000 INTEGER NOT NULL,
        PRIMARY KEY(bucket_start,granularity,host,endpoint,method,status_class,client_type,category)
    )
    """,
    "CREATE INDEX IF NOT EXISTS ix_analytics_buckets_time ON analytics_buckets(granularity,bucket_start DESC)",
    """
    CREATE TABLE IF NOT EXISTS saved_views (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
        kind TEXT NOT NULL CHECK (kind IN ('logs','analytics')),
        name TEXT NOT NULL,
        query_json TEXT NOT NULL,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        UNIQUE(user_id,kind,name)
    )
    """,
)


@dataclass(slots=True)
class AnalyticsFilters:
    host: str = ""
    endpoint: str = ""
    method: str = ""
    status: str = ""
    remote_ip: str = ""
    client_type: str = ""
    category: str = ""
    search: str = ""
    min_duration_ms: float | None = None
    max_duration_ms: float | None = None

    @classmethod
    def from_query(cls, query: dict[str, list[str]]) -> "AnalyticsFilters":
        def first(name: str) -> str:
            return str(query.get(name, [""])[0]).strip()

        def number(name: str) -> float | None:
            value = first(name)
            if not value:
                return None
            try:
                return max(0.0, float(value))
            except ValueError:
                return None

        return cls(
            host=first("host"),
            endpoint=first("endpoint"),
            method=first("method").upper(),
            status=first("status"),
            remote_ip=first("ip"),
            client_type=first("client"),
            category=first("category"),
            search=first("q"),
            min_duration_ms=number("min_ms"),
            max_duration_ms=number("max_ms"),
        )

    def as_query(self) -> dict[str, str]:
        values = {
            "host": self.host,
            "endpoint": self.endpoint,
            "method": self.method,
            "status": self.status,
            "ip": self.remote_ip,
            "client": self.client_type,
            "category": self.category,
            "q": self.search,
            "min_ms": "" if self.min_duration_ms is None else f"{self.min_duration_ms:g}",
            "max_ms": "" if self.max_duration_ms is None else f"{self.max_duration_ms:g}",
        }
        return {key: value for key, value in values.items() if value != ""}


def analytics_settings(database: Database) -> dict[str, Any]:
    value = database.setting("analytics", {}) or {}
    return {
        "raw_retention_days": max(30, int(value.get("raw_retention_days", RAW_RETENTION_DAYS) or RAW_RETENTION_DAYS)),
        "aggregate_retention_days": max(30, int(value.get("aggregate_retention_days", AGGREGATE_RETENTION_DAYS) or AGGREGATE_RETENTION_DAYS)),
        "performance": {
            "normal_ms": max(1, int(value.get("performance", {}).get("normal_ms", 500) or 500)),
            "warning_ms": max(1, int(value.get("performance", {}).get("warning_ms", 1000) or 1000)),
            "slow_ms": max(1, int(value.get("performance", {}).get("slow_ms", 3000) or 3000)),
        },
        "redacted_query_names": sorted({str(item).lower() for item in value.get("redacted_query_names", []) if str(item).strip()}),
    }


def redact_uri(uri: str, extra_names: Iterable[str] = ()) -> str:
    parsed = urllib.parse.urlsplit(uri)
    extra = {name.lower() for name in extra_names}
    query = urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
    safe = []
    for key, value in query:
        lowered = key.lower()
        sensitive = lowered in extra or any(word in lowered for word in SENSITIVE_QUERY_WORDS)
        safe.append((key, "[redacted]" if sensitive else value))
    return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, parsed.path, urllib.parse.urlencode(safe), ""))


def normalize_endpoint(path: str) -> str:
    if not path:
        return "/"
    segments = []
    for segment in path.split("/"):
        if not segment:
            segments.append("")
            continue
        decoded = urllib.parse.unquote(segment)
        if decoded.isdigit() or UUID_SEGMENT_RE.fullmatch(decoded) or OPAQUE_ID_RE.fullmatch(decoded):
            segments.append("{id}")
        else:
            segments.append(segment)
    result = "/".join(segments)
    return result or "/"


def classify_client(user_agent: str) -> str:
    value = user_agent.strip()
    if not value:
        return "unknown"
    if value.lower().startswith(("caddy-ui-health/", "caddy-ui/")):
        return "internal"
    if BOT_RE.search(value):
        return "bot"
    return "human"


def classify_category(path: str, headers: dict[str, Any] | None = None) -> str:
    headers = headers or {}
    upgrade = _header_value(headers, "Upgrade").lower()
    if upgrade == "websocket":
        return "websocket"
    suffix = Path(path).suffix.lower()
    if suffix in ASSET_EXTENSIONS:
        return "asset"
    if path == "/api" or path.startswith("/api/"):
        return "api"
    if path.startswith("/") and not suffix:
        return "page"
    return "other"


def _header_value(headers: dict[str, Any], name: str) -> str:
    for key, value in headers.items():
        if key.lower() != name.lower():
            continue
        if isinstance(value, list):
            return str(value[0]) if value else ""
        return str(value)
    return ""


def _timestamp(value: Any) -> tuple[float, str]:
    try:
        timestamp = float(value)
    except (TypeError, ValueError):
        timestamp = datetime.now(UTC).timestamp()
    return timestamp, datetime.fromtimestamp(timestamp, UTC).isoformat(timespec="milliseconds")


def parse_access_log_items(path: Path, limit: int = 50_000, extra_redactions: Iterable[str] = ()) -> list[dict[str, Any]]:
    values: list[dict[str, Any]] = []
    for line in tail_lines(path, max_bytes=32 * 1024 * 1024, limit=limit):
        try:
            item = json.loads(line)
        except json.JSONDecodeError:
            continue
        request = item.get("request") if isinstance(item.get("request"), dict) else {}
        headers = request.get("headers") if isinstance(request.get("headers"), dict) else {}
        uri = redact_uri(str(request.get("uri", "")), extra_redactions)
        parsed_uri = urllib.parse.urlsplit(uri)
        path_value = parsed_uri.path or "/"
        timestamp, occurred_at = _timestamp(item.get("ts"))
        user_agent = _header_value(headers, "User-Agent")[:500]
        remote_ip = str(request.get("client_ip") or request.get("remote_ip") or "")[:80]
        source_hash = hashlib.sha256(line.encode("utf-8", errors="replace")).hexdigest()
        values.append(
            {
                "source_hash": source_hash,
                "occurred_at": occurred_at,
                "occurred_ts": timestamp,
                "host": str(request.get("host", ""))[:255],
                "method": str(request.get("method", ""))[:16].upper(),
                "uri": uri[:3000],
                "path": path_value[:2000],
                "endpoint": normalize_endpoint(path_value)[:2000],
                "status": int(item.get("status", 0) or 0),
                "bytes_sent": int(item.get("size", 0) or 0),
                "duration_ms": max(0.0, float(item.get("duration", 0) or 0) * 1000.0),
                "remote_ip": remote_ip,
                "user_agent": user_agent,
                "client_type": classify_client(user_agent),
                "category": classify_category(path_value, headers),
            }
        )
    values.sort(key=lambda entry: (entry["occurred_ts"], entry["source_hash"]))
    return values


def _duration_histogram(duration_ms: float) -> tuple[int, int, int, int, int, int, int]:
    return (
        int(duration_ms < 100),
        int(100 <= duration_ms < 250),
        int(250 <= duration_ms < 500),
        int(500 <= duration_ms < 1000),
        int(1000 <= duration_ms < 3000),
        int(3000 <= duration_ms < 10000),
        int(duration_ms >= 10000),
    )


def _anonymize_ip(value: str) -> str:
    try:
        address = ipaddress.ip_address(value)
    except ValueError:
        return "unknown"
    if address.version == 4:
        network = ipaddress.ip_network(f"{address}/24", strict=False)
    else:
        network = ipaddress.ip_network(f"{address}/48", strict=False)
    return f"{network.network_address}/{network.prefixlen}"


class AnalyticsRepository:
    def __init__(self, database: Database):
        self.database = database
        self.ensure_schema()

    def ensure_schema(self) -> None:
        current = int(self.database.setting("feature_schema_analytics", 0) or 0)
        if current >= FEATURE_SCHEMA_VERSION:
            return
        self.database.backup("pre-analytics-migration")
        with self.database.transaction() as connection:
            for statement in ANALYTICS_SCHEMA:
                connection.execute(statement)
            result = connection.execute("PRAGMA integrity_check").fetchone()[0]
            if result != "ok":
                raise RuntimeError(f"Analytics migration integrity check failed: {result}")
        self.database.set_setting("feature_schema_analytics", FEATURE_SCHEMA_VERSION)

    def ingest(self, path: Path) -> int:
        settings = analytics_settings(self.database)
        entries = parse_access_log_items(path, extra_redactions=settings["redacted_query_names"])
        inserted = 0
        with self.database.transaction() as connection:
            for item in entries:
                cursor = connection.execute(
                    """INSERT OR IGNORE INTO request_events(
                       source_hash,occurred_at,occurred_ts,host,method,uri,path,endpoint,status,bytes_sent,duration_ms,
                       remote_ip,user_agent,client_type,category)
                       VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                    (
                        item["source_hash"], item["occurred_at"], item["occurred_ts"], item["host"], item["method"],
                        item["uri"], item["path"], item["endpoint"], item["status"], item["bytes_sent"], item["duration_ms"],
                        item["remote_ip"], item["user_agent"], item["client_type"], item["category"],
                    ),
                )
                if cursor.rowcount != 1:
                    continue
                inserted += 1
                self._upsert_hour(connection, item)
        return inserted

    @staticmethod
    def _upsert_hour(connection: sqlite3.Connection, item: dict[str, Any]) -> None:
        instant = datetime.fromtimestamp(float(item["occurred_ts"]), UTC).replace(minute=0, second=0, microsecond=0)
        status = int(item["status"])
        status_class = f"{status // 100}xx" if status else "unknown"
        histogram = _duration_histogram(float(item["duration_ms"]))
        connection.execute(
            """INSERT INTO analytics_buckets(
               bucket_start,granularity,host,endpoint,method,status_class,client_type,category,requests,bytes_sent,
               duration_sum_ms,duration_max_ms,lt_100,lt_250,lt_500,lt_1000,lt_3000,lt_10000,ge_10000)
               VALUES(?,?,?,?,?,?,?,?,1,?,?,?,?,?,?,?,?,?,?)
               ON CONFLICT(bucket_start,granularity,host,endpoint,method,status_class,client_type,category)
               DO UPDATE SET requests=requests+1,bytes_sent=bytes_sent+excluded.bytes_sent,
                 duration_sum_ms=duration_sum_ms+excluded.duration_sum_ms,
                 duration_max_ms=MAX(duration_max_ms,excluded.duration_max_ms),
                 lt_100=lt_100+excluded.lt_100,lt_250=lt_250+excluded.lt_250,lt_500=lt_500+excluded.lt_500,
                 lt_1000=lt_1000+excluded.lt_1000,lt_3000=lt_3000+excluded.lt_3000,
                 lt_10000=lt_10000+excluded.lt_10000,ge_10000=ge_10000+excluded.ge_10000""",
            (
                instant.isoformat(), "hour", item["host"] or "unknown", item["endpoint"] or "/", item["method"] or "UNKNOWN",
                status_class, item["client_type"], item["category"], int(item["bytes_sent"]), float(item["duration_ms"]),
                float(item["duration_ms"]), *histogram,
            ),
        )

    def compact(self) -> None:
        settings = analytics_settings(self.database)
        raw_cutoff = datetime.now(UTC) - timedelta(days=settings["raw_retention_days"])
        aggregate_cutoff = datetime.now(UTC) - timedelta(days=settings["aggregate_retention_days"])
        day_cutoff = raw_cutoff.replace(hour=0, minute=0, second=0, microsecond=0).isoformat()
        with self.database.transaction() as connection:
            rows = connection.execute(
                """SELECT substr(bucket_start,1,10) AS day,host,endpoint,method,status_class,client_type,category,
                          SUM(requests) requests,SUM(bytes_sent) bytes_sent,SUM(duration_sum_ms) duration_sum_ms,
                          MAX(duration_max_ms) duration_max_ms,SUM(lt_100) lt_100,SUM(lt_250) lt_250,
                          SUM(lt_500) lt_500,SUM(lt_1000) lt_1000,SUM(lt_3000) lt_3000,
                          SUM(lt_10000) lt_10000,SUM(ge_10000) ge_10000
                   FROM analytics_buckets WHERE granularity='hour' AND bucket_start<?
                   GROUP BY day,host,endpoint,method,status_class,client_type,category""",
                (day_cutoff,),
            ).fetchall()
            for row in rows:
                connection.execute(
                    """INSERT INTO analytics_buckets(
                       bucket_start,granularity,host,endpoint,method,status_class,client_type,category,requests,bytes_sent,
                       duration_sum_ms,duration_max_ms,lt_100,lt_250,lt_500,lt_1000,lt_3000,lt_10000,ge_10000)
                       VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                       ON CONFLICT(bucket_start,granularity,host,endpoint,method,status_class,client_type,category)
                       DO UPDATE SET requests=excluded.requests,bytes_sent=excluded.bytes_sent,
                         duration_sum_ms=excluded.duration_sum_ms,duration_max_ms=excluded.duration_max_ms,
                         lt_100=excluded.lt_100,lt_250=excluded.lt_250,lt_500=excluded.lt_500,lt_1000=excluded.lt_1000,
                         lt_3000=excluded.lt_3000,lt_10000=excluded.lt_10000,ge_10000=excluded.ge_10000""",
                    (
                        f"{row['day']}T00:00:00+00:00", "day", row["host"], row["endpoint"], row["method"], row["status_class"],
                        row["client_type"], row["category"], row["requests"], row["bytes_sent"], row["duration_sum_ms"],
                        row["duration_max_ms"], row["lt_100"], row["lt_250"], row["lt_500"], row["lt_1000"], row["lt_3000"],
                        row["lt_10000"], row["ge_10000"],
                    ),
                )
            connection.execute("DELETE FROM analytics_buckets WHERE granularity='hour' AND bucket_start<?", (day_cutoff,))
            connection.execute("DELETE FROM request_events WHERE occurred_at<?", (raw_cutoff.isoformat(),))
            connection.execute("DELETE FROM analytics_buckets WHERE bucket_start<?", (aggregate_cutoff.isoformat(),))

    @staticmethod
    def resolve_range(range_name: str, start: str = "", end: str = "") -> tuple[datetime, datetime]:
        now = datetime.now(UTC)
        durations = {
            "15m": timedelta(minutes=15),
            "1h": timedelta(hours=1),
            "6h": timedelta(hours=6),
            "24h": timedelta(days=1),
            "7d": timedelta(days=7),
            "30d": timedelta(days=30),
            "1y": timedelta(days=365),
        }
        if range_name == "custom" and start:
            try:
                start_value = datetime.fromisoformat(start.replace("Z", "+00:00"))
                end_value = datetime.fromisoformat(end.replace("Z", "+00:00")) if end else now
                if start_value.tzinfo is None:
                    start_value = start_value.replace(tzinfo=UTC)
                if end_value.tzinfo is None:
                    end_value = end_value.replace(tzinfo=UTC)
                return start_value.astimezone(UTC), min(end_value.astimezone(UTC), now)
            except ValueError:
                pass
        duration = durations.get(range_name, durations["24h"])
        return now - duration, now

    @staticmethod
    def _raw_where(filters: AnalyticsFilters, start: datetime, end: datetime) -> tuple[str, list[Any]]:
        clauses = ["occurred_at>=?", "occurred_at<=?"]
        args: list[Any] = [start.isoformat(), end.isoformat()]
        if filters.host:
            clauses.append("host=?")
            args.append(filters.host)
        if filters.endpoint:
            clauses.append("endpoint=?")
            args.append(filters.endpoint)
        if filters.method:
            clauses.append("method=?")
            args.append(filters.method)
        if filters.status:
            if filters.status.endswith("xx") and filters.status[:1].isdigit():
                base = int(filters.status[0]) * 100
                clauses.append("status>=? AND status<?")
                args.extend([base, base + 100])
            elif filters.status.isdigit():
                clauses.append("status=?")
                args.append(int(filters.status))
        if filters.remote_ip:
            clauses.append("remote_ip=?")
            args.append(filters.remote_ip)
        if filters.client_type in {"human", "bot", "internal", "unknown"}:
            clauses.append("client_type=?")
            args.append(filters.client_type)
        if filters.category in {"page", "api", "asset", "websocket", "other"}:
            clauses.append("category=?")
            args.append(filters.category)
        if filters.min_duration_ms is not None:
            clauses.append("duration_ms>=?")
            args.append(filters.min_duration_ms)
        if filters.max_duration_ms is not None:
            clauses.append("duration_ms<=?")
            args.append(filters.max_duration_ms)
        if filters.search:
            clauses.append("(uri LIKE ? OR user_agent LIKE ? OR host LIKE ? OR remote_ip LIKE ?)")
            token = f"%{filters.search}%"
            args.extend([token, token, token, token])
        return " AND ".join(clauses), args

    def events(
        self,
        filters: AnalyticsFilters,
        start: datetime,
        end: datetime,
        limit: int = 500,
        offset: int = 0,
    ) -> list[sqlite3.Row]:
        where, args = self._raw_where(filters, start, end)
        with self.database.connect() as connection:
            return connection.execute(
                f"SELECT * FROM request_events WHERE {where} ORDER BY occurred_at DESC,id DESC LIMIT ? OFFSET ?",
                (*args, min(max(limit, 1), 5000), max(offset, 0)),
            ).fetchall()

    def count_events(self, filters: AnalyticsFilters, start: datetime, end: datetime) -> int:
        where, args = self._raw_where(filters, start, end)
        with self.database.connect() as connection:
            return int(connection.execute(f"SELECT COUNT(*) FROM request_events WHERE {where}", args).fetchone()[0])

    def summary(self, filters: AnalyticsFilters, start: datetime, end: datetime) -> dict[str, Any]:
        raw_cutoff = datetime.now(UTC) - timedelta(days=analytics_settings(self.database)["raw_retention_days"])
        if start >= raw_cutoff:
            return self._raw_summary(filters, start, end)
        return self._bucket_summary(filters, start, end)

    def _raw_summary(self, filters: AnalyticsFilters, start: datetime, end: datetime) -> dict[str, Any]:
        where, args = self._raw_where(filters, start, end)
        with self.database.connect() as connection:
            row = connection.execute(
                f"""SELECT COUNT(*) requests,COALESCE(SUM(bytes_sent),0) bytes_sent,
                           COALESCE(AVG(duration_ms),0) avg_ms,COALESCE(MAX(duration_ms),0) max_ms,
                           SUM(CASE WHEN status BETWEEN 400 AND 499 THEN 1 ELSE 0 END) errors_4xx,
                           SUM(CASE WHEN status>=500 THEN 1 ELSE 0 END) errors_5xx
                    FROM request_events WHERE {where}""",
                args,
            ).fetchone()
            count = int(row["requests"] or 0)
            percentiles = {
                "p50_ms": self._raw_percentile(connection, where, args, count, 0.50),
                "p95_ms": self._raw_percentile(connection, where, args, count, 0.95),
                "p99_ms": self._raw_percentile(connection, where, args, count, 0.99),
            }
        return {**dict(row), **percentiles}

    @staticmethod
    def _raw_percentile(connection: sqlite3.Connection, where: str, args: list[Any], count: int, percentile: float) -> float:
        if count <= 0:
            return 0.0
        offset = max(0, min(count - 1, int(round((count - 1) * percentile))))
        row = connection.execute(
            f"SELECT duration_ms FROM request_events WHERE {where} ORDER BY duration_ms LIMIT 1 OFFSET ?",
            (*args, offset),
        ).fetchone()
        return float(row[0]) if row else 0.0

    def _bucket_where(self, filters: AnalyticsFilters, start: datetime, end: datetime) -> tuple[str, list[Any]]:
        clauses = ["bucket_start>=?", "bucket_start<=?"]
        args: list[Any] = [start.isoformat(), end.isoformat()]
        if filters.host:
            clauses.append("host=?")
            args.append(filters.host)
        if filters.endpoint:
            clauses.append("endpoint=?")
            args.append(filters.endpoint)
        if filters.method:
            clauses.append("method=?")
            args.append(filters.method)
        if filters.status and filters.status.endswith("xx"):
            clauses.append("status_class=?")
            args.append(filters.status)
        if filters.client_type:
            clauses.append("client_type=?")
            args.append(filters.client_type)
        if filters.category:
            clauses.append("category=?")
            args.append(filters.category)
        return " AND ".join(clauses), args

    def _bucket_summary(self, filters: AnalyticsFilters, start: datetime, end: datetime) -> dict[str, Any]:
        where, args = self._bucket_where(filters, start, end)
        with self.database.connect() as connection:
            row = connection.execute(
                f"""SELECT COALESCE(SUM(requests),0) requests,COALESCE(SUM(bytes_sent),0) bytes_sent,
                           COALESCE(SUM(duration_sum_ms),0) duration_sum_ms,COALESCE(MAX(duration_max_ms),0) max_ms,
                           COALESCE(SUM(CASE WHEN status_class='4xx' THEN requests ELSE 0 END),0) errors_4xx,
                           COALESCE(SUM(CASE WHEN status_class='5xx' THEN requests ELSE 0 END),0) errors_5xx,
                           COALESCE(SUM(lt_100),0) lt_100,COALESCE(SUM(lt_250),0) lt_250,
                           COALESCE(SUM(lt_500),0) lt_500,COALESCE(SUM(lt_1000),0) lt_1000,
                           COALESCE(SUM(lt_3000),0) lt_3000,COALESCE(SUM(lt_10000),0) lt_10000,
                           COALESCE(SUM(ge_10000),0) ge_10000
                    FROM analytics_buckets WHERE {where}""",
                args,
            ).fetchone()
        requests = int(row["requests"] or 0)
        histogram = [
            (100.0, int(row["lt_100"] or 0)),
            (250.0, int(row["lt_250"] or 0)),
            (500.0, int(row["lt_500"] or 0)),
            (1000.0, int(row["lt_1000"] or 0)),
            (3000.0, int(row["lt_3000"] or 0)),
            (10000.0, int(row["lt_10000"] or 0)),
            (float(row["max_ms"] or 10000), int(row["ge_10000"] or 0)),
        ]
        return {
            "requests": requests,
            "bytes_sent": int(row["bytes_sent"] or 0),
            "avg_ms": float(row["duration_sum_ms"] or 0) / requests if requests else 0.0,
            "max_ms": float(row["max_ms"] or 0),
            "errors_4xx": int(row["errors_4xx"] or 0),
            "errors_5xx": int(row["errors_5xx"] or 0),
            "p50_ms": self._histogram_percentile(histogram, requests, 0.50),
            "p95_ms": self._histogram_percentile(histogram, requests, 0.95),
            "p99_ms": self._histogram_percentile(histogram, requests, 0.99),
        }

    @staticmethod
    def _histogram_percentile(histogram: list[tuple[float, int]], total: int, percentile: float) -> float:
        if total <= 0:
            return 0.0
        target = total * percentile
        cumulative = 0
        for boundary, count in histogram:
            cumulative += count
            if cumulative >= target:
                return boundary
        return histogram[-1][0] if histogram else 0.0

    def series(self, filters: AnalyticsFilters, start: datetime, end: datetime) -> list[dict[str, Any]]:
        duration = end - start
        if duration <= timedelta(hours=6):
            where, args = self._raw_where(filters, start, end)
            expression = "substr(occurred_at,1,16)"
            with self.database.connect() as connection:
                rows = connection.execute(
                    f"""SELECT {expression} bucket,COUNT(*) requests,AVG(duration_ms) avg_ms,
                               SUM(CASE WHEN status>=400 THEN 1 ELSE 0 END) errors
                        FROM request_events WHERE {where} GROUP BY bucket ORDER BY bucket""",
                    args,
                ).fetchall()
            return [dict(row) for row in rows]
        if duration <= timedelta(days=30):
            where, args = self._bucket_where(filters, start, end)
            with self.database.connect() as connection:
                rows = connection.execute(
                    f"""SELECT substr(bucket_start,1,13) bucket,SUM(requests) requests,
                               CASE WHEN SUM(requests)>0 THEN SUM(duration_sum_ms)/SUM(requests) ELSE 0 END avg_ms,
                               SUM(CASE WHEN status_class IN ('4xx','5xx') THEN requests ELSE 0 END) errors
                        FROM analytics_buckets WHERE granularity='hour' AND {where}
                        GROUP BY bucket ORDER BY bucket""",
                    args,
                ).fetchall()
            return [dict(row) for row in rows]
        where, args = self._bucket_where(filters, start, end)
        with self.database.connect() as connection:
            rows = connection.execute(
                f"""SELECT substr(bucket_start,1,10) bucket,SUM(requests) requests,
                           CASE WHEN SUM(requests)>0 THEN SUM(duration_sum_ms)/SUM(requests) ELSE 0 END avg_ms,
                           SUM(CASE WHEN status_class IN ('4xx','5xx') THEN requests ELSE 0 END) errors
                    FROM analytics_buckets WHERE {where}
                    GROUP BY bucket ORDER BY bucket""",
                args,
            ).fetchall()
        return [dict(row) for row in rows]

    def top(self, column: str, filters: AnalyticsFilters, start: datetime, end: datetime, limit: int = 10) -> list[tuple[str, int, float]]:
        allowed = {"host", "endpoint", "remote_ip", "user_agent"}
        if column not in allowed:
            raise ValueError("Unsupported analytics dimension.")
        raw_cutoff = datetime.now(UTC) - timedelta(days=analytics_settings(self.database)["raw_retention_days"])
        if start < raw_cutoff and column in {"host", "endpoint"}:
            where, args = self._bucket_where(filters, start, end)
            with self.database.connect() as connection:
                rows = connection.execute(
                    f"""SELECT {column} label,SUM(requests) requests,
                               CASE WHEN SUM(requests)>0 THEN SUM(duration_sum_ms)/SUM(requests) ELSE 0 END avg_ms
                        FROM analytics_buckets WHERE {where} GROUP BY {column}
                        ORDER BY requests DESC,label LIMIT ?""",
                    (*args, min(max(limit, 1), 100)),
                ).fetchall()
        else:
            where, args = self._raw_where(filters, max(start, raw_cutoff), end)
            with self.database.connect() as connection:
                rows = connection.execute(
                    f"""SELECT {column} label,COUNT(*) requests,AVG(duration_ms) avg_ms
                        FROM request_events WHERE {where} AND {column}<>'' GROUP BY {column}
                        ORDER BY requests DESC,label LIMIT ?""",
                    (*args, min(max(limit, 1), 100)),
                ).fetchall()
        return [(str(row["label"]), int(row["requests"]), float(row["avg_ms"] or 0)) for row in rows]

    def slow_endpoints(self, filters: AnalyticsFilters, start: datetime, end: datetime, limit: int = 10) -> list[tuple[str, int, float]]:
        where, args = self._raw_where(filters, start, end)
        with self.database.connect() as connection:
            rows = connection.execute(
                f"""SELECT endpoint,COUNT(*) requests,AVG(duration_ms) avg_ms
                    FROM request_events WHERE {where} AND category<>'asset'
                    GROUP BY endpoint HAVING COUNT(*)>=1 ORDER BY avg_ms DESC LIMIT ?""",
                (*args, min(max(limit, 1), 100)),
            ).fetchall()
        return [(str(row["endpoint"]), int(row["requests"]), float(row["avg_ms"] or 0)) for row in rows]

    def dimensions(self, start: datetime, end: datetime) -> dict[str, list[str]]:
        with self.database.connect() as connection:
            hosts = [row[0] for row in connection.execute(
                "SELECT DISTINCT host FROM request_events WHERE occurred_at>=? AND occurred_at<=? AND host<>'' ORDER BY host LIMIT 500",
                (start.isoformat(), end.isoformat()),
            ).fetchall()]
            endpoints = [row[0] for row in connection.execute(
                "SELECT endpoint FROM request_events WHERE occurred_at>=? AND occurred_at<=? GROUP BY endpoint ORDER BY COUNT(*) DESC LIMIT 500",
                (start.isoformat(), end.isoformat()),
            ).fetchall()]
        return {"hosts": hosts, "endpoints": endpoints}

    def client_detail(self, ip: str, start: datetime, end: datetime) -> dict[str, Any]:
        filters = AnalyticsFilters(remote_ip=ip)
        return {
            "ip": ip,
            "summary": self.summary(filters, start, end),
            "series": self.series(filters, start, end),
            "endpoints": self.top("endpoint", filters, start, end, 20),
            "hosts": self.top("host", filters, start, end, 20),
            "events": self.events(filters, start, end, 100),
        }

    def save_view(self, user_id: str, kind: str, name: str, query: dict[str, str]) -> None:
        if kind not in {"logs", "analytics"}:
            raise ValueError("Invalid saved view kind.")
        name = name.strip()
        if not name or len(name) > 80:
            raise ValueError("Saved view name is required and must be at most 80 characters.")
        now = utc_now()
        with self.database.transaction() as connection:
            connection.execute(
                """INSERT INTO saved_views(user_id,kind,name,query_json,created_at,updated_at) VALUES(?,?,?,?,?,?)
                   ON CONFLICT(user_id,kind,name) DO UPDATE SET query_json=excluded.query_json,updated_at=excluded.updated_at""",
                (user_id, kind, name, json.dumps(query, separators=(",", ":"), sort_keys=True), now, now),
            )

    def saved_views(self, user_id: str, kind: str) -> list[sqlite3.Row]:
        with self.database.connect() as connection:
            return connection.execute(
                "SELECT * FROM saved_views WHERE user_id=? AND kind=? ORDER BY name COLLATE NOCASE",
                (user_id, kind),
            ).fetchall()

    def delete_view(self, user_id: str, view_id: int) -> None:
        with self.database.transaction() as connection:
            connection.execute("DELETE FROM saved_views WHERE id=? AND user_id=?", (view_id, user_id))

    def export(self, filters: AnalyticsFilters, start: datetime, end: datetime, format_name: str) -> tuple[bytes, str]:
        rows = self.events(filters, start, end, limit=5000)
        fields = [
            "occurred_at", "host", "method", "uri", "endpoint", "status", "bytes_sent", "duration_ms",
            "remote_ip", "user_agent", "client_type", "category",
        ]
        if format_name == "json":
            content = json.dumps([{field: row[field] for field in fields} for row in rows], indent=2).encode("utf-8")
            return content, "application/json"
        buffer = io.StringIO()
        writer = csv.DictWriter(buffer, fieldnames=fields)
        writer.writeheader()
        for row in rows:
            writer.writerow({field: row[field] for field in fields})
        return buffer.getvalue().encode("utf-8"), "text/csv; charset=utf-8"

    def anonymized_network(self, ip: str) -> str:
        return _anonymize_ip(ip)
