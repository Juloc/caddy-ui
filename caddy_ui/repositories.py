from __future__ import annotations

import json
import sqlite3
import uuid
from dataclasses import asdict
from typing import Any

from .db import Database, utc_now
from .domain import AccessGroup, ManagedRoute, Role
from .security import hash_password


class RouteRepository:
    def __init__(self, database: Database):
        self.database = database

    def list(self) -> list[ManagedRoute]:
        with self.database.connect() as connection:
            rows = connection.execute("SELECT config_json FROM routes ORDER BY name COLLATE NOCASE").fetchall()
        return [ManagedRoute.from_json(row[0]) for row in rows]

    def get(self, route_id: str) -> ManagedRoute | None:
        with self.database.connect() as connection:
            row = connection.execute("SELECT config_json FROM routes WHERE id=?", (route_id,)).fetchone()
        return ManagedRoute.from_json(row[0]) if row else None

    def get_by_name(self, name: str) -> ManagedRoute | None:
        with self.database.connect() as connection:
            row = connection.execute("SELECT config_json FROM routes WHERE name=? COLLATE NOCASE", (name,)).fetchone()
        return ManagedRoute.from_json(row[0]) if row else None

    def save(self, route: ManagedRoute) -> None:
        route.validate()
        now = utc_now()
        existing = self.get(route.id)
        route.created_at = existing.created_at if existing else route.created_at or now
        route.updated_at = now
        with self.database.transaction() as connection:
            connection.execute(
                """INSERT INTO routes(id,name,host,kind,enabled,config_json,created_at,updated_at)
                   VALUES(?,?,?,?,?,?,?,?)
                   ON CONFLICT(id) DO UPDATE SET name=excluded.name,host=excluded.host,kind=excluded.kind,
                     enabled=excluded.enabled,config_json=excluded.config_json,updated_at=excluded.updated_at""",
                (
                    route.id,
                    route.name,
                    route.effective_host,
                    route.kind.value,
                    int(route.enabled),
                    route.to_json(),
                    route.created_at,
                    route.updated_at,
                ),
            )

    def delete(self, route_id: str) -> None:
        with self.database.transaction() as connection:
            connection.execute("DELETE FROM routes WHERE id=?", (route_id,))


class AccessRepository:
    def __init__(self, database: Database):
        self.database = database

    def list_groups(self) -> list[AccessGroup]:
        with self.database.connect() as connection:
            rows = connection.execute("SELECT config_json FROM access_groups ORDER BY name COLLATE NOCASE").fetchall()
        return [AccessGroup(**json.loads(row[0])) for row in rows]

    def get_group(self, group_id: str) -> AccessGroup | None:
        with self.database.connect() as connection:
            row = connection.execute("SELECT config_json FROM access_groups WHERE id=?", (group_id,)).fetchone()
        return AccessGroup(**json.loads(row[0])) if row else None

    def save_group(self, group: AccessGroup) -> None:
        group.validate()
        now = utc_now()
        config = json.dumps(asdict(group), separators=(",", ":"), sort_keys=True)
        with self.database.transaction() as connection:
            connection.execute(
                """INSERT INTO access_groups(id,name,config_json,created_at,updated_at) VALUES(?,?,?,?,?)
                   ON CONFLICT(id) DO UPDATE SET name=excluded.name,config_json=excluded.config_json,updated_at=excluded.updated_at""",
                (group.id, group.name, config, now, now),
            )

    def delete_group(self, group_id: str) -> None:
        with self.database.transaction() as connection:
            used = connection.execute(
                "SELECT COUNT(*) FROM routes WHERE json_extract(config_json, '$.access_group_id')=?",
                (group_id,),
            ).fetchone()[0]
            if used:
                raise ValueError("Access group is still assigned to one or more routes.")
            connection.execute("DELETE FROM access_groups WHERE id=?", (group_id,))

    def list_credentials(self, group_id: str) -> list[sqlite3.Row]:
        with self.database.connect() as connection:
            return connection.execute(
                "SELECT id,group_id,username,enabled,created_at,updated_at FROM access_credentials WHERE group_id=? ORDER BY username COLLATE NOCASE",
                (group_id,),
            ).fetchall()

    def save_credential(self, group_id: str, username: str, password: str, credential_id: str = "") -> str:
        if not username.strip() or len(username) > 80:
            raise ValueError("Username is required and must be at most 80 characters.")
        credential_id = credential_id or str(uuid.uuid4())
        now = utc_now()
        with self.database.transaction() as connection:
            if password:
                password_value = hash_password(password)
            else:
                existing = connection.execute(
                    "SELECT password_hash FROM access_credentials WHERE id=? AND group_id=?",
                    (credential_id, group_id),
                ).fetchone()
                if not existing:
                    raise ValueError("A password is required for a new credential.")
                password_value = existing[0]
            connection.execute(
                """INSERT INTO access_credentials(id,group_id,username,password_hash,created_at,updated_at) VALUES(?,?,?,?,?,?)
                   ON CONFLICT(id) DO UPDATE SET username=excluded.username,password_hash=excluded.password_hash,updated_at=excluded.updated_at""",
                (credential_id, group_id, username.strip(), password_value, now, now),
            )
        return credential_id

    def authenticate(self, group_id: str, username: str, password: str) -> sqlite3.Row | None:
        from .security import verify_password

        with self.database.connect() as connection:
            row = connection.execute(
                "SELECT * FROM access_credentials WHERE group_id=? AND username=? COLLATE NOCASE AND enabled=1",
                (group_id, username.strip()),
            ).fetchone()
        return row if row and verify_password(password, row["password_hash"]) else None

    def delete_credential(self, credential_id: str) -> None:
        with self.database.transaction() as connection:
            connection.execute("DELETE FROM access_credentials WHERE id=?", (credential_id,))


class UserRepository:
    def __init__(self, database: Database):
        self.database = database

    def list(self) -> list[sqlite3.Row]:
        with self.database.connect() as connection:
            return connection.execute(
                "SELECT id,username,display_name,role,enabled,totp_enabled,theme,created_at,updated_at,last_login_at FROM users ORDER BY username COLLATE NOCASE"
            ).fetchall()

    def get(self, user_id: str) -> sqlite3.Row | None:
        with self.database.connect() as connection:
            return connection.execute("SELECT * FROM users WHERE id=?", (user_id,)).fetchone()

    def save(
        self,
        username: str,
        display_name: str,
        role: Role,
        password: str,
        user_id: str = "",
        enabled: bool = True,
    ) -> str:
        if not username.strip() or len(username) > 80:
            raise ValueError("Username is required and must be at most 80 characters.")
        user_id = user_id or str(uuid.uuid4())
        now = utc_now()
        with self.database.transaction() as connection:
            if password:
                password_value = hash_password(password)
            else:
                existing = connection.execute("SELECT password_hash FROM users WHERE id=?", (user_id,)).fetchone()
                if not existing:
                    raise ValueError("A password is required for a new user.")
                password_value = existing[0]
            if not enabled:
                admin_count = connection.execute("SELECT COUNT(*) FROM users WHERE role='admin' AND enabled=1 AND id<>?", (user_id,)).fetchone()[0]
                existing_role = connection.execute("SELECT role FROM users WHERE id=?", (user_id,)).fetchone()
                if existing_role and existing_role[0] == Role.ADMIN.value and admin_count == 0:
                    raise ValueError("The last enabled administrator cannot be disabled.")
            existing_role = connection.execute("SELECT role,enabled FROM users WHERE id=?", (user_id,)).fetchone()
            if existing_role and existing_role["role"] == Role.ADMIN.value and existing_role["enabled"] and role != Role.ADMIN:
                other_admins = connection.execute("SELECT COUNT(*) FROM users WHERE role='admin' AND enabled=1 AND id<>?", (user_id,)).fetchone()[0]
                if not other_admins:
                    raise ValueError("The last enabled administrator cannot be demoted.")
            connection.execute(
                """INSERT INTO users(id,username,display_name,password_hash,role,enabled,created_at,updated_at)
                   VALUES(?,?,?,?,?,?,?,?)
                   ON CONFLICT(id) DO UPDATE SET username=excluded.username,display_name=excluded.display_name,
                     password_hash=excluded.password_hash,role=excluded.role,enabled=excluded.enabled,updated_at=excluded.updated_at""",
                (user_id, username.strip(), display_name.strip() or username.strip(), password_value, role.value, int(enabled), now, now),
            )
        return user_id

    def delete(self, user_id: str) -> None:
        with self.database.transaction() as connection:
            row = connection.execute("SELECT role,enabled FROM users WHERE id=?", (user_id,)).fetchone()
            if not row:
                return
            if row["role"] == Role.ADMIN.value and row["enabled"]:
                other_admins = connection.execute(
                    "SELECT COUNT(*) FROM users WHERE role='admin' AND enabled=1 AND id<>?",
                    (user_id,),
                ).fetchone()[0]
                if not other_admins:
                    raise ValueError("The last enabled administrator cannot be deleted.")
            connection.execute("DELETE FROM users WHERE id=?", (user_id,))


class ProviderRepository:
    def __init__(self, database: Database):
        self.database = database

    def list(self) -> list[dict[str, Any]]:
        with self.database.connect() as connection:
            rows = connection.execute("SELECT * FROM providers ORDER BY label COLLATE NOCASE").fetchall()
        return [self._decode(row) for row in rows]

    def get(self, provider_id: str) -> dict[str, Any] | None:
        with self.database.connect() as connection:
            row = connection.execute("SELECT * FROM providers WHERE id=?", (provider_id,)).fetchone()
        return self._decode(row) if row else None

    @staticmethod
    def _decode(row: sqlite3.Row) -> dict[str, Any]:
        value = json.loads(row["config_json"])
        value.update({"id": row["id"], "type": row["type"], "label": row["label"]})
        return value

    def save(self, provider: dict[str, Any]) -> None:
        provider_id = str(provider.get("id", "")).strip()
        if not provider_id or not provider_id.replace("-", "").replace("_", "").isalnum():
            raise ValueError("Provider ID must use letters, numbers, dashes, or underscores.")
        provider_type = str(provider.get("type", "netcup"))
        if provider_type != "netcup":
            raise ValueError("Only Netcup is currently implemented.")
        label = str(provider.get("label", provider_id)).strip()
        config = {key: value for key, value in provider.items() if key not in {"id", "type", "label"}}
        now = utc_now()
        with self.database.transaction() as connection:
            connection.execute(
                """INSERT INTO providers(id,type,label,config_json,created_at,updated_at) VALUES(?,?,?,?,?,?)
                   ON CONFLICT(id) DO UPDATE SET type=excluded.type,label=excluded.label,config_json=excluded.config_json,updated_at=excluded.updated_at""",
                (provider_id, provider_type, label, json.dumps(config, separators=(",", ":"), sort_keys=True), now, now),
            )

    def delete(self, provider_id: str) -> None:
        with self.database.transaction() as connection:
            connection.execute("DELETE FROM providers WHERE id=?", (provider_id,))
