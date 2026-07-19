from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any

from .audit import Actor, AuditLog
from .config import Settings
from .db import Database, utc_now
from .domain import ManagedRoute, RouteKind, Upstream
from .repositories import ProviderRepository, RouteRepository


META_PREFIX = "# caddy-ui-route:"


def _digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _already_imported(database: Database, source: str, digest: str) -> bool:
    with database.connect() as connection:
        row = connection.execute("SELECT source_digest FROM migration_state WHERE source=?", (source,)).fetchone()
    return bool(row and row[0] == digest)


def _mark_imported(database: Database, source: str, digest: str) -> None:
    with database.transaction() as connection:
        connection.execute(
            """INSERT INTO migration_state(source,imported_at,source_digest) VALUES(?,?,?)
               ON CONFLICT(source) DO UPDATE SET imported_at=excluded.imported_at,source_digest=excluded.source_digest""",
            (source, utc_now(), digest),
        )


def import_legacy(settings: Settings, database: Database, audit: AuditLog) -> dict[str, int]:
    counts = {"providers": 0, "routes": 0}
    provider_repository = ProviderRepository(database)
    route_repository = RouteRepository(database)
    sources = [settings.legacy_config_path, *sorted(settings.routes_dir.glob("*.caddy"))]
    existing = [path for path in sources if path.is_file()]
    if not existing:
        return counts
    database.backup("pre-legacy-import")

    config_path = settings.legacy_config_path
    if config_path.is_file():
        digest = _digest(config_path)
        source = str(config_path)
        if not _already_imported(database, source, digest):
            data = json.loads(config_path.read_text(encoding="utf-8"))
            for provider in data.get("providers", []):
                if not provider_repository.get(str(provider.get("id", ""))):
                    provider_repository.save(dict(provider))
                    counts["providers"] += 1
            legacy_settings = data.get("settings")
            if isinstance(legacy_settings, dict) and legacy_settings.get("domain"):
                database.set_setting("default_domain", str(legacy_settings["domain"]).strip().rstrip("."))
            _mark_imported(database, source, digest)

    for path in sorted(settings.routes_dir.glob("*.caddy")):
        digest = _digest(path)
        source = str(path)
        if _already_imported(database, source, digest):
            continue
        metadata: dict[str, Any] | None = None
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.startswith(META_PREFIX):
                metadata = json.loads(line[len(META_PREFIX) :].strip())
                break
        if metadata:
            name = str(metadata.get("name") or path.stem)
            if not route_repository.get_by_name(name):
                route = ManagedRoute(
                    name=name,
                    domain=str(database.setting("default_domain", settings.default_domain)),
                    host=str(metadata.get("host", "")),
                    kind=RouteKind.PROXY,
                    upstreams=[Upstream(str(metadata["upstream"]))],
                    tls_skip_verify=bool(metadata.get("tls_skip_verify", False)),
                )
                route_repository.save(route)
                counts["routes"] += 1
        _mark_imported(database, source, digest)

    audit.record(Actor(), "legacy.import", "migration", "legacy", after=counts)
    return counts
