from __future__ import annotations

import json
import re
import secrets
import sqlite3
from dataclasses import dataclass
from typing import Any

from .db import Database, utc_now


SECRET_KEY = re.compile(r"password|secret|token|api[_-]?key|credential|hash", re.IGNORECASE)


def redact(value: Any) -> Any:
    if isinstance(value, dict):
        sensitive_header = str(value.get("name", "")).lower() in {"authorization", "cookie", "set-cookie", "proxy-authorization"}
        return {
            key: "[redacted]" if SECRET_KEY.search(str(key)) or str(key) == "custom_snippet" or (sensitive_header and str(key) == "value") else redact(item)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [redact(item) for item in value]
    return value


@dataclass(slots=True)
class Actor:
    user_id: str = ""
    username: str = "system"
    remote_address: str = ""

    @classmethod
    def from_session(cls, session: sqlite3.Row | None, remote_address: str = "") -> "Actor":
        if not session:
            return cls(remote_address=remote_address)
        return cls(str(session["user_id"]), str(session["username"]), remote_address)


class AuditLog:
    def __init__(self, database: Database):
        self.database = database

    def record(
        self,
        actor: Actor,
        action: str,
        object_type: str,
        object_id: str,
        before: Any = None,
        after: Any = None,
        result: str = "success",
        revision_id: str = "",
        correlation_id: str = "",
    ) -> None:
        correlation_id = correlation_id or secrets.token_hex(12)
        with self.database.transaction() as connection:
            connection.execute(
                """INSERT INTO audit_events(
                       occurred_at,actor_user_id,actor_username,remote_address,action,object_type,object_id,
                       before_json,after_json,result,revision_id,correlation_id
                   ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
                (
                    utc_now(),
                    actor.user_id or None,
                    actor.username,
                    actor.remote_address,
                    action,
                    object_type,
                    object_id,
                    json.dumps(redact(before), separators=(",", ":"), sort_keys=True),
                    json.dumps(redact(after), separators=(",", ":"), sort_keys=True),
                    result,
                    revision_id or None,
                    correlation_id,
                ),
            )

    def list(self, limit: int = 200, offset: int = 0) -> list[sqlite3.Row]:
        with self.database.connect() as connection:
            return connection.execute(
                "SELECT * FROM audit_events ORDER BY occurred_at DESC, id DESC LIMIT ? OFFSET ?",
                (min(max(limit, 1), 1000), max(offset, 0)),
            ).fetchall()
