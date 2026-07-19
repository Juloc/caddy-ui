from __future__ import annotations

import json
import sqlite3
import threading
from contextlib import contextmanager
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Iterator

from .config import Settings
from .domain import Permission, ROLE_PERMISSIONS, Role
from .security import hash_password, new_session_tokens, token_hash, verify_password


SCHEMA_VERSION = 2


SCHEMA = """
CREATE TABLE IF NOT EXISTS users (
    id TEXT PRIMARY KEY,
    username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    display_name TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    role TEXT NOT NULL CHECK (role IN ('admin', 'editor', 'viewer')),
    enabled INTEGER NOT NULL DEFAULT 1,
    totp_secret TEXT NOT NULL DEFAULT '',
    totp_enabled INTEGER NOT NULL DEFAULT 0,
    theme TEXT NOT NULL DEFAULT 'system' CHECK (theme IN ('system', 'light', 'dark')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_login_at TEXT
);
CREATE TABLE IF NOT EXISTS sessions (
    token_hash TEXT PRIMARY KEY,
    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    csrf_token TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    remote_address TEXT NOT NULL DEFAULT '',
    user_agent TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_sessions_expires ON sessions(expires_at);
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value_json TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS providers (
    id TEXT PRIMARY KEY,
    type TEXT NOT NULL,
    label TEXT NOT NULL,
    config_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS routes (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    host TEXT NOT NULL,
    kind TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    config_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS access_groups (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    config_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS access_credentials (
    id TEXT PRIMARY KEY,
    group_id TEXT NOT NULL REFERENCES access_groups(id) ON DELETE CASCADE,
    username TEXT NOT NULL COLLATE NOCASE,
    password_hash TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(group_id, username)
);
CREATE TABLE IF NOT EXISTS portal_sessions (
    token_hash TEXT PRIMARY KEY,
    credential_id TEXT NOT NULL REFERENCES access_credentials(id) ON DELETE CASCADE,
    group_id TEXT NOT NULL REFERENCES access_groups(id) ON DELETE CASCADE,
    expires_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS revisions (
    id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL,
    actor_user_id TEXT REFERENCES users(id),
    reason TEXT NOT NULL,
    manifest_json TEXT NOT NULL,
    content_json TEXT NOT NULL,
    digest TEXT NOT NULL,
    applied INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS audit_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at TEXT NOT NULL,
    actor_user_id TEXT REFERENCES users(id),
    actor_username TEXT NOT NULL,
    remote_address TEXT NOT NULL,
    action TEXT NOT NULL,
    object_type TEXT NOT NULL,
    object_id TEXT NOT NULL,
    before_json TEXT NOT NULL,
    after_json TEXT NOT NULL,
    result TEXT NOT NULL,
    revision_id TEXT,
    correlation_id TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_audit_occurred ON audit_events(occurred_at DESC);
CREATE TABLE IF NOT EXISTS notifications (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL,
    severity TEXT NOT NULL,
    event_type TEXT NOT NULL,
    title TEXT NOT NULL,
    message TEXT NOT NULL,
    object_type TEXT NOT NULL DEFAULT '',
    object_id TEXT NOT NULL DEFAULT '',
    acknowledged_at TEXT
);
CREATE TABLE IF NOT EXISTS traffic_buckets (
    bucket_start TEXT NOT NULL,
    granularity TEXT NOT NULL CHECK (granularity IN ('hour', 'day', 'month')),
    host TEXT NOT NULL,
    status_class TEXT NOT NULL,
    requests INTEGER NOT NULL,
    bytes_sent INTEGER NOT NULL,
    PRIMARY KEY(bucket_start, granularity, host, status_class)
);
CREATE TABLE IF NOT EXISTS migration_state (
    source TEXT PRIMARY KEY,
    imported_at TEXT NOT NULL,
    source_digest TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS route_previews (
    id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    route_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_route_previews_expires ON route_previews(expires_at);
"""


def utc_now() -> str:
    return datetime.now(UTC).isoformat(timespec="seconds")


class Database:
    def __init__(self, settings: Settings):
        self.settings = settings
        self.path = settings.database_path
        self._write_lock = threading.RLock()

    def connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(self.path, timeout=10, isolation_level=None)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA foreign_keys=ON")
        connection.execute("PRAGMA journal_mode=WAL")
        connection.execute("PRAGMA synchronous=NORMAL")
        connection.execute("PRAGMA busy_timeout=5000")
        return connection

    @contextmanager
    def transaction(self) -> Iterator[sqlite3.Connection]:
        with self._write_lock, self.connect() as connection:
            connection.execute("BEGIN IMMEDIATE")
            try:
                yield connection
            except Exception:
                connection.rollback()
                raise
            else:
                connection.commit()

    def initialize(self) -> None:
        self.settings.ensure_directories()
        existed = self.path.exists()
        if existed:
            with self.connect() as connection:
                current = int(connection.execute("PRAGMA user_version").fetchone()[0])
            if current > SCHEMA_VERSION:
                raise RuntimeError(f"Database schema {current} is newer than supported version {SCHEMA_VERSION}.")
            if current < SCHEMA_VERSION:
                self.backup("pre-migration")
        try:
            with self.transaction() as connection:
                connection.executescript(SCHEMA)
                connection.execute(f"PRAGMA user_version={SCHEMA_VERSION}")
        except Exception:
            if not existed:
                self.path.unlink(missing_ok=True)
            raise
        self._bootstrap_admin()

    def backup(self, reason: str) -> Path | None:
        if not self.path.exists():
            return None
        self.settings.backup_dir.mkdir(parents=True, exist_ok=True)
        safe_reason = "".join(character if character.isalnum() or character in "-_" else "-" for character in reason)
        timestamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%S%fZ")
        target = self.settings.backup_dir / f"caddy-ui-{timestamp}-{safe_reason}.db"
        with self.connect() as source, sqlite3.connect(target) as destination:
            source.backup(destination)
        return target

    def restore(self, backup: Path) -> None:
        resolved = backup.resolve()
        if self.settings.backup_dir.resolve() not in resolved.parents:
            raise ValueError("Backup path is outside the configured backup directory.")
        if not resolved.is_file():
            raise ValueError("Backup does not exist.")
        with sqlite3.connect(resolved) as connection:
            result = connection.execute("PRAGMA integrity_check").fetchone()[0]
            if result != "ok":
                raise RuntimeError(f"Backup integrity check failed: {result}")
        self.backup("pre-restore")
        with self._write_lock, sqlite3.connect(resolved) as source, self.connect() as destination:
            source.backup(destination)
            result = destination.execute("PRAGMA integrity_check").fetchone()[0]
            if result != "ok":
                raise RuntimeError(f"Restored database integrity check failed: {result}")

    def _bootstrap_admin(self) -> None:
        with self.connect() as connection:
            count = int(connection.execute("SELECT COUNT(*) FROM users").fetchone()[0])
        if count:
            return
        if not self.settings.bootstrap_password:
            raise RuntimeError("CADDY_UI_PASSWORD is required to create the first administrator.")
        import uuid

        now = utc_now()
        with self.transaction() as connection:
            connection.execute(
                "INSERT INTO users(id, username, display_name, password_hash, role, created_at, updated_at) VALUES(?,?,?,?,?,?,?)",
                (
                    str(uuid.uuid4()),
                    self.settings.bootstrap_username,
                    self.settings.bootstrap_username,
                    hash_password(self.settings.bootstrap_password),
                    Role.ADMIN.value,
                    now,
                    now,
                ),
            )

    def authenticate(self, username: str, password: str) -> sqlite3.Row | None:
        with self.connect() as connection:
            row = connection.execute(
                "SELECT * FROM users WHERE username=? COLLATE NOCASE AND enabled=1",
                (username.strip(),),
            ).fetchone()
        if not row or not verify_password(password, row["password_hash"]):
            return None
        return row

    def create_session(self, user_id: str, ttl_seconds: int, remote_address: str, user_agent: str) -> tuple[str, str]:
        from datetime import timedelta

        token, hashed, csrf = new_session_tokens()
        now = datetime.now(UTC)
        with self.transaction() as connection:
            connection.execute("DELETE FROM sessions WHERE expires_at < ?", (now.isoformat(),))
            connection.execute(
                "INSERT INTO sessions(token_hash,user_id,csrf_token,created_at,expires_at,remote_address,user_agent) VALUES(?,?,?,?,?,?,?)",
                (
                    hashed,
                    user_id,
                    csrf,
                    now.isoformat(),
                    (now + timedelta(seconds=ttl_seconds)).isoformat(),
                    remote_address,
                    user_agent[:400],
                ),
            )
            connection.execute("UPDATE users SET last_login_at=?, updated_at=? WHERE id=?", (now.isoformat(), now.isoformat(), user_id))
        return token, csrf

    def session(self, token: str) -> sqlite3.Row | None:
        if not token:
            return None
        now = utc_now()
        with self.connect() as connection:
            return connection.execute(
                """SELECT sessions.*, users.username, users.display_name, users.role, users.enabled, users.theme,
                          (SELECT value_json FROM settings WHERE key='accent') AS accent_json
                   FROM sessions JOIN users ON users.id=sessions.user_id
                   WHERE sessions.token_hash=? AND sessions.expires_at>? AND users.enabled=1""",
                (token_hash(token), now),
            ).fetchone()

    def revoke_session(self, token: str) -> None:
        with self.transaction() as connection:
            connection.execute("DELETE FROM sessions WHERE token_hash=?", (token_hash(token),))

    @staticmethod
    def permitted(session: sqlite3.Row, permission: Permission) -> bool:
        try:
            return permission in ROLE_PERMISSIONS[Role(session["role"])]
        except (KeyError, ValueError):
            return False

    def setting(self, key: str, default: Any = None) -> Any:
        with self.connect() as connection:
            row = connection.execute("SELECT value_json FROM settings WHERE key=?", (key,)).fetchone()
        return json.loads(row[0]) if row else default

    def set_setting(self, key: str, value: Any) -> None:
        with self.transaction() as connection:
            connection.execute(
                """INSERT INTO settings(key,value_json,updated_at) VALUES(?,?,?)
                   ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json,updated_at=excluded.updated_at""",
                (key, json.dumps(value, separators=(",", ":"), sort_keys=True), utc_now()),
            )
