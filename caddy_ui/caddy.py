from __future__ import annotations

import difflib
import hashlib
import json
import os
import re
import shutil
import tempfile
import threading
import urllib.error
import urllib.request
import uuid
from dataclasses import asdict
from pathlib import Path

from .audit import Actor, AuditLog
from .config import Settings
from .db import Database, utc_now
from .domain import HeaderOperation, ManagedRoute, RouteKind
from .repositories import ProviderRepository, RouteRepository


MANAGED_HEADER = "# managed-by caddy-ui"
META_PREFIX = "# caddy-ui-route:"
SENSITIVE_CONFIG_LINE = re.compile(r"(?im)^([ +\-]*[^\n]*(?:password|secret|token|api[_-]?key|authorization|cookie)[^\s]*\s+).*$")
DEFAULT_CADDYFILE = """{
    email {$ACME_EMAIL}
    admin 0.0.0.0:2019
    log default {
        output file /var/log/caddy/caddy.log {
            roll_size 10mb
            roll_keep 5
        }
        format json
    }
}

import /etc/caddy/routes/site-*.caddy
"""
LEGACY_CADDYFILE_MARKERS = (
    "{$DOMAIN}, *.{$DOMAIN} {",
    "import /etc/caddy/routes/*.caddy",
    'respond "Service not configured" 404',
)


def is_legacy_caddyfile(value: str) -> bool:
    return all(marker in value for marker in LEGACY_CADDYFILE_MARKERS)


def redact_config(value: str) -> str:
    return SENSITIVE_CONFIG_LINE.sub(r"\1[redacted]", value)


def _safe_identifier(value: str) -> str:
    return "".join(character if character.isalnum() or character == "_" else "_" for character in value)


def _quote(value: str) -> str:
    if any(character.isspace() or character in '{}"' for character in value):
        return json.dumps(value)
    return value


def _render_headers(directive: str, operations: list[HeaderOperation], indent: str = "    ") -> list[str]:
    if not operations:
        return []
    lines = [f"{indent}{directive} {{"]
    for operation in operations:
        if operation.operation == "delete":
            lines.append(f"{indent}    -{operation.name}")
        elif operation.operation == "add":
            lines.append(f"{indent}    +{operation.name} {_quote(operation.value)}")
        else:
            lines.append(f"{indent}    {operation.name} {_quote(operation.value)}")
    lines.append(f"{indent}}}")
    return lines


def render_route(route: ManagedRoute) -> str:
    route.validate()
    metadata = json.dumps(
        {"id": route.id, "name": route.name, "kind": route.kind.value, "host": route.host},
        separators=(",", ":"),
        sort_keys=True,
    )
    if not route.enabled:
        return f"{MANAGED_HEADER}\n{META_PREFIX} {metadata}\n# disabled\n"
    matcher = f"caddy_ui_{_safe_identifier(route.id)}"
    matchers = [f"host {route.effective_host}"]
    if route.paths:
        matchers.append("path " + " ".join(_quote(path) for path in route.paths))
    lines = [f"    {META_PREFIX} {metadata}", f"    @{matcher} {{"]
    lines.extend(f"        {item}" for item in matchers)
    lines.extend(["    }", f"    handle @{matcher} {{"])

    if route.access_group_id:
        lines.extend(
            [
                "        handle /__caddy_ui_auth/* {",
                "            reverse_proxy caddy-ui:8098",
                "        }",
                "        forward_auth caddy-ui:8098 {",
                f"            uri /portal/authorize?group={route.access_group_id}",
                "            copy_headers Remote-User",
                "        }",
            ]
        )

    if route.kind == RouteKind.REDIRECT:
        lines.append(f"        redir {_quote(route.redirect_to)} {route.redirect_status}")
    elif route.kind == RouteKind.CUSTOM:
        for custom_line in route.custom_snippet.strip().splitlines():
            lines.append(f"        {custom_line.rstrip()}")
    else:
        lines.extend(_render_headers("header", route.response_headers, "        "))
        addresses = " ".join(_quote(upstream.address) for upstream in route.upstreams)
        lines.append(f"        reverse_proxy {addresses} {{")
        if len(route.upstreams) > 1:
            lines.extend(["            lb_policy " + route.load_balancing])
        if route.health_uri:
            lines.extend([f"            health_uri {_quote(route.health_uri)}", f"            health_interval {route.health_interval}"])
        lines.extend(_render_headers("header_up", route.request_headers, "            "))
        if route.tls_skip_verify:
            lines.extend(["            transport http {", "                tls_insecure_skip_verify", "            }"])
        lines.append("        }")
    lines.extend(["    }", ""])
    return "\n".join(lines)


def render_site(host: str, routes: list[ManagedRoute], tls_lines: list[str] | None = None) -> str:
    lines = [MANAGED_HEADER, f"{host} {{", "    encode zstd gzip"]
    if tls_lines:
        lines.extend(["    tls {", *[f"        {line}" for line in tls_lines], "    }"])
    lines.extend(
        [
            "    log {",
            "        output file /var/log/caddy/access.log {",
            "            roll_size 10mb",
            "            roll_keep 5",
            "        }",
            "        format json",
            "    }",
        ]
    )
    for route in routes:
        if route.enabled:
            lines.extend(render_route(route).splitlines())
        else:
            lines.append(f"    # disabled route: {route.name}")
    lines.extend(["    handle {", '        respond "Service not configured" 404', "    }", "}", ""])
    return "\n".join(lines)


class CaddyManager:
    def __init__(self, settings: Settings, database: Database, audit: AuditLog):
        self.settings = settings
        self.database = database
        self.audit = audit
        self.routes = RouteRepository(database)
        self.providers = ProviderRepository(database)
        self._lock = threading.RLock()

    def rendered(self) -> dict[str, str]:
        return self._rendered_for(self.routes.list())

    def _rendered_for(self, routes: list[ManagedRoute]) -> dict[str, str]:
        grouped: dict[str, list[ManagedRoute]] = {}
        for route in routes:
            grouped.setdefault(route.effective_host, []).append(route)
        content: dict[str, str] = {}
        providers = self.providers.list()
        for host, routes in sorted(grouped.items()):
            enabled = [route for route in routes if route.enabled]
            catch_all = [route for route in enabled if not route.paths]
            if len(catch_all) > 1:
                names = ", ".join(sorted(route.name for route in catch_all))
                raise ValueError(f"Host {host} has multiple catch-all routes: {names}.")
            claimed_paths: dict[str, str] = {}
            for route in enabled:
                for path in route.paths:
                    if path in claimed_paths:
                        raise ValueError(f"Path {path} on {host} is used by both {claimed_paths[path]} and {route.name}.")
                    claimed_paths[path] = route.name
            routes = sorted(routes, key=lambda route: (not bool(route.paths), -max((len(path) for path in route.paths), default=0), route.name.lower()))
            tls_lines: list[str] = []
            domain = routes[0].domain
            provider = next((item for item in providers if domain in item.get("domains", []) and item.get("type") == "netcup"), None)
            if provider:
                tls_lines = [
                    "dns netcup {",
                    f"    customer_number {provider.get('customer_number', '{env.NETCUP_CUSTOMER_NUMBER}')}",
                    f"    api_key {provider.get('api_key', '{env.NETCUP_API_KEY}')}",
                    f"    api_password {provider.get('api_password', '{env.NETCUP_API_PASSWORD}')}",
                    "}",
                    "propagation_timeout 600s",
                    "resolvers 1.1.1.1 8.8.8.8",
                ]
            digest = hashlib.sha256(host.encode("utf-8")).hexdigest()[:12]
            content[f"site-{digest}.caddy"] = render_site(host, routes, tls_lines)
        return content

    def preview(self, proposed: ManagedRoute | None = None, delete_id: str = "") -> tuple[str, str]:
        current = self.rendered()
        routes = self.routes.list()
        if proposed:
            proposed.validate()
            routes = [route for route in routes if route.id != proposed.id]
            routes.append(proposed)
        if delete_id:
            routes = [route for route in routes if route.id != delete_id]
        proposed_files = self._rendered_for(routes)
        before = "".join(f"--- {name}\n{value}" for name, value in sorted(current.items()))
        after = "".join(f"--- {name}\n{value}" for name, value in sorted(proposed_files.items()))
        diff = "".join(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile="current", tofile="proposed"))
        return redact_config(after), redact_config(diff)

    def migrate_legacy_layout(self, actor: Actor | None = None) -> bool:
        if not self.settings.caddyfile_path.is_file():
            return False
        previous_caddyfile = self.settings.caddyfile_path.read_text(encoding="utf-8")
        if not is_legacy_caddyfile(previous_caddyfile):
            return False
        actor = actor or Actor()
        self.database.backup("pre-caddy-layout-migration")
        backup_path = self.settings.caddyfile_path.with_name(f"{self.settings.caddyfile_path.name}.pre-1.0")
        if not backup_path.exists():
            backup_path.write_text(previous_caddyfile, encoding="utf-8", newline="\n")
        with tempfile.TemporaryDirectory(prefix="caddy-ui-layout-") as temporary_name:
            routes_backup = Path(temporary_name) / "routes"
            if self.settings.routes_dir.exists():
                shutil.copytree(self.settings.routes_dir, routes_backup)
            try:
                content = self.rendered()
                self._write_managed_files(content)
                staging = self.settings.caddyfile_path.with_suffix(".tmp")
                staging.write_text(DEFAULT_CADDYFILE, encoding="utf-8", newline="\n")
                staging.replace(self.settings.caddyfile_path)
                self.validate()
                if self.settings.auto_reload:
                    self.reload()
                revision_id = self._create_revision(actor, "Migrate pre-1.0 Caddy layout", content)
                self._mark_revision_applied(revision_id)
                self.audit.record(actor, "caddy_layout.migrate", "migration", "pre-1.0", revision_id=revision_id)
                return True
            except Exception:
                self.settings.caddyfile_path.write_text(previous_caddyfile, encoding="utf-8", newline="\n")
                self._restore_directory(routes_backup)
                try:
                    if self.settings.auto_reload:
                        self.reload()
                except Exception:
                    pass
                raise

    def apply(self, actor: Actor, reason: str, proposed: ManagedRoute | None = None, delete_id: str = "") -> str:
        with self._lock:
            return self._apply(actor, reason, proposed, delete_id)

    def _apply(self, actor: Actor, reason: str, proposed: ManagedRoute | None = None, delete_id: str = "") -> str:
        before_route = self.routes.get(proposed.id) if proposed else self.routes.get(delete_id)
        self.database.backup("pre-route-change")
        with tempfile.TemporaryDirectory(prefix="caddy-ui-") as temporary_name:
            temporary = Path(temporary_name)
            backup = temporary / "routes"
            if self.settings.routes_dir.exists():
                shutil.copytree(self.settings.routes_dir, backup)
            if proposed:
                self.routes.save(proposed)
            elif delete_id:
                self.routes.delete(delete_id)
            revision_id = ""
            try:
                content = self.rendered()
                revision_id = self._create_revision(actor, reason, content)
                self._write_managed_files(content)
                self.validate()
                if self.settings.auto_reload:
                    self.reload()
                self._mark_revision_applied(revision_id)
                self.audit.record(
                    actor,
                    "route.delete" if delete_id else "route.save",
                    "route",
                    delete_id or proposed.id,
                    before=asdict(before_route) if before_route else None,
                    after=asdict(proposed) if proposed else None,
                    revision_id=revision_id,
                )
                return revision_id
            except Exception as exc:
                if proposed:
                    if before_route:
                        self.routes.save(before_route)
                    else:
                        self.routes.delete(proposed.id)
                elif before_route:
                    self.routes.save(before_route)
                self._restore_directory(backup)
                try:
                    if self.settings.auto_reload:
                        self.reload()
                except Exception:
                    pass
                self.audit.record(
                    actor,
                    "route.apply",
                    "route",
                    delete_id or (proposed.id if proposed else ""),
                    before=asdict(before_route) if before_route else None,
                    after=asdict(proposed) if proposed else None,
                    result=f"failed: {exc}",
                    revision_id=revision_id,
                )
                raise

    def _write_managed_files(self, content: dict[str, str]) -> None:
        self.settings.routes_dir.mkdir(parents=True, exist_ok=True)
        expected = set(content)
        for path in self.settings.routes_dir.glob("*.caddy"):
            try:
                first = path.read_text(encoding="utf-8").splitlines()[0]
            except (OSError, IndexError, UnicodeError):
                continue
            if first == MANAGED_HEADER and path.name not in expected:
                path.unlink()
        for filename, value in content.items():
            target = self.settings.routes_dir / filename
            staging = target.with_suffix(".tmp")
            staging.write_text(value, encoding="utf-8", newline="\n")
            staging.replace(target)

    def _restore_directory(self, backup: Path) -> None:
        shutil.rmtree(self.settings.routes_dir, ignore_errors=True)
        if backup.exists():
            shutil.copytree(backup, self.settings.routes_dir)
        else:
            self.settings.routes_dir.mkdir(parents=True, exist_ok=True)

    def validate(self) -> None:
        status, body = self._admin_post("/adapt", self.settings.caddyfile_path.read_bytes())
        if status == 0 or status >= 300:
            raise RuntimeError(f"Caddy validation failed: {body}")

    def reload(self) -> None:
        status, body = self._admin_post("/load", self.settings.caddyfile_path.read_bytes())
        if status == 0 or status >= 300:
            raise RuntimeError(f"Caddy reload failed: {body}")
        request = urllib.request.Request(f"{self.settings.caddy_admin_url}/config/", method="GET")
        try:
            with urllib.request.urlopen(request, timeout=5) as response:
                if response.status >= 400:
                    raise RuntimeError(f"Caddy health verification returned HTTP {response.status}.")
        except urllib.error.URLError as exc:
            raise RuntimeError(f"Caddy health verification failed: {exc}") from exc

    def _admin_post(self, path: str, body: bytes) -> tuple[int, str]:
        request = urllib.request.Request(
            f"{self.settings.caddy_admin_url}{path}",
            data=body,
            headers={
                "Content-Type": "text/caddyfile",
                "Cache-Control": "must-revalidate",
                "User-Agent": "caddy-ui/1.0",
            },
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.status, response.read(1024 * 1024).decode("utf-8", errors="replace")
        except urllib.error.HTTPError as exc:
            return exc.code, exc.read(1024 * 1024).decode("utf-8", errors="replace")
        except urllib.error.URLError as exc:
            return 0, str(exc)

    def _create_revision(self, actor: Actor, reason: str, content: dict[str, str]) -> str:
        revision_id = str(uuid.uuid4())
        payload_value = {
            "files": content,
            "routes": [json.loads(route.to_json()) for route in self.routes.list()],
        }
        payload = json.dumps(payload_value, separators=(",", ":"), sort_keys=True)
        digest = hashlib.sha256(payload.encode("utf-8")).hexdigest()
        manifest = {"files": sorted(content), "digest": digest}
        with self.database.transaction() as connection:
            connection.execute(
                "INSERT INTO revisions(id,created_at,actor_user_id,reason,manifest_json,content_json,digest) VALUES(?,?,?,?,?,?,?)",
                (revision_id, utc_now(), actor.user_id or None, reason, json.dumps(manifest), payload, digest),
            )
        return revision_id

    def _mark_revision_applied(self, revision_id: str) -> None:
        with self.database.transaction() as connection:
            connection.execute("UPDATE revisions SET applied=1 WHERE id=?", (revision_id,))

    def list_revisions(self, limit: int = 100):
        with self.database.connect() as connection:
            return connection.execute(
                "SELECT revisions.*, users.username FROM revisions LEFT JOIN users ON users.id=revisions.actor_user_id ORDER BY created_at DESC LIMIT ?",
                (min(max(limit, 1), 500),),
            ).fetchall()

    def restore_revision(self, actor: Actor, revision_id: str) -> None:
        with self._lock:
            self._restore_revision(actor, revision_id)

    def _restore_revision(self, actor: Actor, revision_id: str) -> None:
        with self.database.connect() as connection:
            row = connection.execute("SELECT content_json FROM revisions WHERE id=? AND applied=1", (revision_id,)).fetchone()
        if not row:
            raise ValueError("Revision not found.")
        snapshot = json.loads(row[0])
        content = snapshot.get("files", snapshot)
        routes = [ManagedRoute.from_json(item) for item in snapshot.get("routes", [])]
        backup = self.database.backup("pre-revision-restore")
        with tempfile.TemporaryDirectory(prefix="caddy-ui-restore-") as temporary_name:
            routes_backup = Path(temporary_name) / "routes"
            if self.settings.routes_dir.exists():
                shutil.copytree(self.settings.routes_dir, routes_backup)
            try:
                with self.database.transaction() as connection:
                    connection.execute("DELETE FROM routes")
                for route in routes:
                    self.routes.save(route)
                self._write_managed_files(content)
                self.validate()
                if self.settings.auto_reload:
                    self.reload()
                new_revision = self._create_revision(actor, f"Restore {revision_id}", content)
                self._mark_revision_applied(new_revision)
                self.audit.record(actor, "revision.restore", "revision", revision_id, after={"new_revision": new_revision})
            except Exception:
                if backup:
                    self.database.restore(backup)
                self._restore_directory(routes_backup)
                try:
                    if self.settings.auto_reload:
                        self.reload()
                except Exception:
                    pass
                raise
