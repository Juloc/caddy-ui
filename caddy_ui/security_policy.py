from __future__ import annotations

import hashlib
import ipaddress
import json
import os
import re
import urllib.parse
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import Any, Iterable

from .db import utc_now


INTERNAL_SECRET_HEADER = "X-Caddy-UI-Proxy-Secret"
REFERENCE_RE = re.compile(r"^[A-Za-z0-9-]{1,64}$")


def _env_bool(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None or not value.strip() or value.strip().lower() == "auto":
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def _env_int(name: str, default: int, minimum: int, maximum: int) -> int:
    try:
        value = int(os.getenv(name, str(default)))
    except ValueError as exc:
        raise RuntimeError(f"{name} must be an integer.") from exc
    return min(maximum, max(minimum, value))


def _split_cidrs(value: str) -> tuple[ipaddress._BaseNetwork, ...]:
    result: list[ipaddress._BaseNetwork] = []
    for item in re.split(r"[\s,]+", value.strip()):
        if item:
            result.append(ipaddress.ip_network(item, strict=False))
    if not result:
        raise RuntimeError("CADDY_UI_TRUSTED_PROXY_CIDRS must contain at least one CIDR.")
    return tuple(result)


@dataclass(frozen=True, slots=True)
class SecurityPolicy:
    public_url: str
    public_scheme: str
    public_host: str
    trusted_proxy_networks: tuple[ipaddress._BaseNetwork, ...]
    proxy_secret: str
    require_totp: bool
    bind_session_ip: bool
    portal_session_ttl_seconds: int
    throttle_window_seconds: int
    account_attempts: int
    address_attempts: int

    @classmethod
    def from_environment(cls) -> "SecurityPolicy":
        public_url = os.getenv("CADDY_UI_PUBLIC_URL", "").strip().rstrip("/")
        public_scheme = ""
        public_host = ""
        if public_url:
            try:
                parsed = urllib.parse.urlsplit(public_url)
                port = parsed.port
            except ValueError as exc:
                raise RuntimeError("CADDY_UI_PUBLIC_URL is malformed.") from exc
            if (
                parsed.scheme != "https"
                or not parsed.hostname
                or port not in {None, 443}
                or parsed.path not in {"", "/"}
                or parsed.query
                or parsed.fragment
                or parsed.username
                or parsed.password
            ):
                raise RuntimeError(
                    "CADDY_UI_PUBLIC_URL must be a standard HTTPS origin without credentials, port, path, query, or fragment."
                )
            public_scheme = parsed.scheme
            public_host = normalize_host(parsed.netloc)
        proxy_secret = os.getenv("CADDY_UI_PROXY_SECRET", "")
        if proxy_secret and (
            not re.fullmatch(r"[A-Za-z0-9_-]{32,128}", proxy_secret)
            or proxy_secret.startswith("replace_with_")
        ):
            raise RuntimeError("CADDY_UI_PROXY_SECRET must be a generated value with 32 to 128 URL-safe characters.")
        if public_url and not proxy_secret:
            raise RuntimeError("CADDY_UI_PROXY_SECRET is required when CADDY_UI_PUBLIC_URL is configured.")
        networks = _split_cidrs(
            os.getenv(
                "CADDY_UI_TRUSTED_PROXY_CIDRS",
                "127.0.0.0/8,::1/128,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16",
            )
        )
        return cls(
            public_url=public_url,
            public_scheme=public_scheme,
            public_host=public_host,
            trusted_proxy_networks=networks,
            proxy_secret=proxy_secret,
            require_totp=_env_bool("CADDY_UI_REQUIRE_TOTP", bool(public_url)),
            bind_session_ip=_env_bool("CADDY_UI_BIND_SESSION_IP", False),
            portal_session_ttl_seconds=_env_int("CADDY_UI_PORTAL_SESSION_TTL", 28800, 300, 604800),
            throttle_window_seconds=_env_int("CADDY_UI_LOGIN_WINDOW", 900, 60, 86400),
            account_attempts=_env_int("CADDY_UI_LOGIN_ACCOUNT_ATTEMPTS", 8, 3, 100),
            address_attempts=_env_int("CADDY_UI_LOGIN_ADDRESS_ATTEMPTS", 30, 5, 500),
        )

    def trusted_proxy(self, address: str) -> bool:
        try:
            value = ipaddress.ip_address(address)
        except ValueError:
            return False
        return any(value in network for network in self.trusted_proxy_networks)

    def context_hash(self) -> str:
        material = "\0".join(
            (
                self.public_url,
                self.proxy_secret,
                str(self.require_totp),
                str(self.bind_session_ip),
            )
        )
        return hashlib.sha256(material.encode("utf-8")).hexdigest()


def normalize_host(value: str) -> str:
    value = value.strip().lower().rstrip(".")
    if not value or any(character in value for character in "\r\n/\\"):
        return ""
    try:
        parsed = urllib.parse.urlsplit("//" + value)
        port = parsed.port
    except ValueError:
        return ""
    if parsed.username or parsed.password or not parsed.hostname:
        return ""
    host = parsed.hostname.rstrip(".")
    if port and port not in {80, 443}:
        return f"{host}:{port}"
    return host


def safe_return_path(value: str) -> str:
    if not value.startswith("/") or value.startswith("//") or "\\" in value or any(ord(character) < 32 for character in value):
        return "/"
    try:
        parsed = urllib.parse.urlsplit(value)
    except ValueError:
        return "/"
    if parsed.scheme or parsed.netloc:
        return "/"
    return urllib.parse.urlunsplit(("", "", parsed.path or "/", parsed.query, ""))


class PersistentLoginThrottle:
    def __init__(self, database: Any, policy: SecurityPolicy):
        self.database = database
        self.policy = policy
        self._initialize()
        self._synchronize_context()

    @staticmethod
    def _digest(key: str) -> str:
        return hashlib.sha256(key.encode("utf-8")).hexdigest()

    def _initialize(self) -> None:
        version = int(self.database.setting("security_schema_version", 0) or 0)
        if version >= 2:
            return
        self.database.backup("pre-security-hardening")
        with self.database.transaction() as connection:
            connection.execute(
                """CREATE TABLE IF NOT EXISTS auth_failures (
                       key_hash TEXT NOT NULL,
                       failed_at INTEGER NOT NULL
                   )"""
            )
            connection.execute(
                "CREATE INDEX IF NOT EXISTS ix_auth_failures_key_time ON auth_failures(key_hash, failed_at)"
            )
            columns = {
                str(row[1])
                for row in connection.execute("PRAGMA table_info(portal_sessions)").fetchall()
            }
            for name in ("created_at", "remote_address", "user_agent"):
                if name not in columns:
                    connection.execute(
                        f"ALTER TABLE portal_sessions ADD COLUMN {name} TEXT NOT NULL DEFAULT ''"
                    )
            connection.execute("DELETE FROM sessions")
            connection.execute("DELETE FROM portal_sessions")
            connection.execute(
                """INSERT INTO settings(key,value_json,updated_at) VALUES('security_schema_version','2',?)
                   ON CONFLICT(key) DO UPDATE SET value_json='2',updated_at=excluded.updated_at""",
                (utc_now(),),
            )

    def _synchronize_context(self) -> None:
        current = self.policy.context_hash()
        previous = str(self.database.setting("security_context_hash", "") or "")
        if previous == current:
            return
        with self.database.transaction() as connection:
            connection.execute("DELETE FROM sessions")
            connection.execute("DELETE FROM portal_sessions")
            connection.execute(
                """INSERT INTO settings(key,value_json,updated_at) VALUES('security_context_hash',?,?)
                   ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json,updated_at=excluded.updated_at""",
                (json.dumps(current), utc_now()),
            )

    def allowed(self, keys: Iterable[tuple[str, int]]) -> bool:
        now = int(datetime.now(UTC).timestamp())
        cutoff = now - self.policy.throttle_window_seconds
        with self.database.transaction() as connection:
            connection.execute("DELETE FROM auth_failures WHERE failed_at<=?", (cutoff,))
            for key, maximum in keys:
                count = int(
                    connection.execute(
                        "SELECT COUNT(*) FROM auth_failures WHERE key_hash=? AND failed_at>?",
                        (self._digest(key), cutoff),
                    ).fetchone()[0]
                )
                if count >= maximum:
                    return False
        return True

    def record_failure(self, keys: Iterable[str]) -> None:
        now = int(datetime.now(UTC).timestamp())
        with self.database.transaction() as connection:
            connection.executemany(
                "INSERT INTO auth_failures(key_hash,failed_at) VALUES(?,?)",
                [(self._digest(key), now) for key in keys],
            )

    def clear(self, keys: Iterable[str]) -> None:
        values = [(self._digest(key),) for key in keys]
        if not values:
            return
        with self.database.transaction() as connection:
            connection.executemany("DELETE FROM auth_failures WHERE key_hash=?", values)
