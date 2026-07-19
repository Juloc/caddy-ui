from __future__ import annotations

import io
import json
import logging
import mimetypes
import os
import secrets
import sqlite3
import sys
import urllib.parse
import uuid
import zipfile
from dataclasses import asdict, replace
from http import HTTPStatus
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable

from .audit import Actor, AuditLog
from .caddy import CaddyManager
from .config import Settings
from .db import Database, utc_now
from .domain import AccessGroup, HeaderOperation, ManagedRoute, Permission, Role, RouteKind, Upstream
from .jobs import JobRunner
from .migration import import_legacy
from .monitoring import caddy_status, certificate_files, parse_access_logs, route_health, tail_lines, traffic_summary
from .notifications import NotificationService
from .providers.netcup import NetcupProvider
from .repositories import AccessRepository, ProviderRepository, RouteRepository, UserRepository
from .security import LoginThrottle, new_session_tokens, new_totp_secret, token_hash, verify_totp
from . import __version__, views


SESSION_COOKIE = "caddy_ui_session"
PORTAL_COOKIE_PREFIX = "caddy_portal_"
STATIC_DIR = Path(__file__).with_name("static")


def _lines(value: str) -> list[str]:
    return [line.strip() for line in value.splitlines() if line.strip()]


def _headers(value: str) -> list[HeaderOperation]:
    result: list[HeaderOperation] = []
    for line in _lines(value):
        operation = "set"
        remainder = line
        first, separator, rest = line.partition(" ")
        if first in {"set", "add", "delete"} and separator:
            operation, remainder = first, rest
        name, separator, header_value = remainder.partition(":")
        if not separator and operation != "delete":
            raise ValueError(f"Header must use Name: value syntax: {line}")
        result.append(HeaderOperation(name.strip(), header_value.strip(), operation))
    return result


def route_from_form(form: dict[str, list[str]], default_domain: str, is_admin: bool) -> ManagedRoute:
    kind = RouteKind(first(form, "kind", "proxy"))
    if kind == RouteKind.CUSTOM and not is_admin:
        raise PermissionError("Only administrators can manage Custom Routes.")
    route = ManagedRoute(
        id=first(form, "id") or str(uuid.uuid4()),
        name=first(form, "name").strip(),
        domain=(first(form, "domain") or default_domain).strip().rstrip("."),
        host=first(form, "host").strip().rstrip("."),
        kind=kind,
        enabled=first(form, "enabled") == "1",
        paths=_lines(first(form, "paths")),
        upstreams=[Upstream(item) for item in _lines(first(form, "upstreams"))],
        request_headers=_headers(first(form, "request_headers")),
        response_headers=_headers(first(form, "response_headers")),
        load_balancing=first(form, "load_balancing", "random"),
        health_uri=first(form, "health_uri").strip(),
        health_interval=first(form, "health_interval", "30s").strip(),
        tls_skip_verify=first(form, "tls_skip_verify") == "1",
        redirect_to=first(form, "redirect_to").strip(),
        redirect_status=int(first(form, "redirect_status", "308")),
        access_group_id=first(form, "access_group_id").strip(),
        custom_snippet=first(form, "custom_snippet"),
    )
    route.validate()
    return route


def first(form: dict[str, list[str]], key: str, default: str = "") -> str:
    return form.get(key, [default])[0]


def safe_route_dict(route: ManagedRoute) -> dict[str, Any]:
    value = json.loads(route.to_json())
    if value.get("custom_snippet"):
        value["custom_snippet"] = ""
        value["redacted_custom_snippet"] = True
    for header in value.get("request_headers", []) + value.get("response_headers", []):
        if str(header.get("name", "")).lower() in {"authorization", "cookie", "set-cookie", "proxy-authorization"}:
            header["value"] = "[redacted]"
    return value


class Application:
    def __init__(self, settings: Settings):
        self.settings = settings
        self.database = Database(settings)
        self.database.initialize()
        self.audit = AuditLog(self.database)
        self.routes = RouteRepository(self.database)
        self.access = AccessRepository(self.database)
        self.users = UserRepository(self.database)
        self.providers = ProviderRepository(self.database)
        self.caddy = CaddyManager(settings, self.database, self.audit)
        self.notifications = NotificationService(self.database)
        self.throttle = LoginThrottle()
        import_legacy(settings, self.database, self.audit)
        self.caddy.migrate_legacy_layout()
        self.jobs = JobRunner(settings, self.database, self.notifications)

    def start_jobs(self) -> None:
        self.jobs.start()


class Handler(BaseHTTPRequestHandler):
    server_version = "CaddyUI/1.0"
    app: Application

    def do_GET(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        if parsed.path == "/api/health":
            self._json({"ok": True, "version": __version__})
            return
        if parsed.path in {"/login", "/__caddy_ui_auth/login"}:
            self._login_get(parsed)
            return
        if parsed.path in {"/static/app.css", "/static/app.js", "/__caddy_ui_auth/static/app.css", "/__caddy_ui_auth/static/app.js"}:
            self._static(parsed.path)
            return
        if parsed.path == "/portal/authorize":
            self._portal_authorize(parsed)
            return
        session = self._require_session(api=parsed.path.startswith("/api/"))
        if not session:
            return
        query = urllib.parse.parse_qs(parsed.query)
        message, error = first(query, "message"), first(query, "error")
        try:
            if parsed.path == "/":
                self._dashboard(session, message, error)
            elif parsed.path == "/routes":
                self._routes(session, query, message, error)
            elif parsed.path == "/routes/export":
                self._routes_export(session, query)
            elif parsed.path == "/access":
                self._access(session, message, error)
            elif parsed.path == "/logs":
                self._logs(session, query, message, error)
            elif parsed.path == "/logs/download":
                self._logs_download(session, first(query, "tab", "access"))
            elif parsed.path == "/system":
                self._system(session, message, error)
            elif parsed.path == "/system/diagnostics":
                self._diagnostics(session)
            elif parsed.path == "/dns":
                self._dns(session, query, message, error)
            elif parsed.path == "/admin/users":
                self._users(session, message, error)
            elif parsed.path == "/admin/audit":
                self._audit(session, message, error)
            elif parsed.path == "/admin/settings":
                self._settings(session, message, error)
            elif parsed.path == "/api/routes":
                self._json([safe_route_dict(route) for route in self.app.routes.list()])
            elif parsed.path == "/api/status":
                self._json(caddy_status(self.app.settings))
            else:
                self.send_error(HTTPStatus.NOT_FOUND)
        except PermissionError as exc:
            self.send_error(HTTPStatus.FORBIDDEN, str(exc))
        except Exception as exc:
            logging.exception("GET %s failed", parsed.path)
            self._redirect(parsed.path if not parsed.path.startswith("/api/") else "/", error=str(exc))

    def do_POST(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        try:
            form = self._form()
        except (ValueError, UnicodeDecodeError) as exc:
            self.send_error(HTTPStatus.REQUEST_ENTITY_TOO_LARGE if "2 MiB" in str(exc) else HTTPStatus.BAD_REQUEST, str(exc))
            return
        if parsed.path == "/login":
            self._login_post(form)
            return
        if parsed.path == "/__caddy_ui_auth/login":
            self._portal_login_post(form)
            return
        session = self._require_session(api=False)
        if not session:
            return
        if not secrets.compare_digest(first(form, "csrf"), str(session["csrf_token"])):
            self.send_error(HTTPStatus.FORBIDDEN, "Invalid CSRF token.")
            return
        actor = Actor.from_session(session, self.client_address[0])
        try:
            if parsed.path == "/logout":
                self.app.database.revoke_session(self._cookie(SESSION_COOKIE))
                self._redirect("/login", clear_session=True)
            elif parsed.path == "/routes/preview":
                self._require_permission(session, Permission.MANAGE_ROUTES)
                route = route_from_form(form, self._default_domain(), self._is_admin(session))
                self._validate_access_group(route)
                _, diff = self.app.caddy.preview(proposed=route)
                preview_id = self._store_route_preview(session, route)
                self._routes(session, {"edit": [route.id]}, preview_diff=diff, proposed_json=preview_id, edit_override=route)
            elif parsed.path == "/routes/apply":
                self._require_permission(session, Permission.MANAGE_ROUTES)
                route = self._consume_route_preview(session, first(form, "preview_id"))
                if route.kind == RouteKind.CUSTOM:
                    self._require_permission(session, Permission.MANAGE_CUSTOM_ROUTES)
                self._validate_access_group(route)
                self.app.caddy.apply(actor, f"Save route {route.name}", proposed=route)
                self._redirect("/routes", message=f"Route {route.name} applied.")
            elif parsed.path == "/routes/delete":
                self._require_permission(session, Permission.MANAGE_ROUTES)
                route_id = first(form, "route_id")
                self.app.caddy.apply(actor, "Delete route", delete_id=route_id)
                self._redirect("/routes", message="Route deleted.")
            elif parsed.path == "/routes/bulk":
                self._routes_bulk(session, actor, form)
            elif parsed.path == "/routes/import":
                self._routes_import(session, actor, form)
            elif parsed.path == "/routes/import-custom":
                self._require_permission(session, Permission.MANAGE_CUSTOM_ROUTES)
                route = ManagedRoute(
                    name=first(form, "name").strip(),
                    domain=(first(form, "domain") or self._default_domain()).strip().rstrip("."),
                    host=first(form, "host").strip().rstrip("."),
                    kind=RouteKind.CUSTOM,
                    custom_snippet=first(form, "custom_snippet"),
                )
                route.validate()
                if self.app.routes.get_by_name(route.name):
                    raise ValueError(f"Route {route.name} already exists; import never overwrites.")
                _, diff = self.app.caddy.preview(proposed=route)
                preview_id = self._store_route_preview(session, route)
                self._routes(session, {"edit": [route.id]}, preview_diff=diff, proposed_json=preview_id, edit_override=route)
            elif parsed.path == "/access/save":
                self._require_permission(session, Permission.MANAGE_ACCESS)
                group = AccessGroup(
                    id=first(form, "group_id") or str(uuid.uuid4()),
                    name=first(form, "name"),
                    title=first(form, "title", "Sign in"),
                    help_text=first(form, "help_text"),
                    accent=first(form, "accent", "#0f6cbd"),
                    logo_data=first(form, "logo_data"),
                )
                before = self.app.access.get_group(group.id)
                self.app.access.save_group(group)
                self.app.audit.record(actor, "access_group.update" if before else "access_group.create", "access_group", group.id, before=asdict(before) if before else None, after=asdict(group))
                self._redirect("/access", message="Access group saved.")
            elif parsed.path == "/access/delete":
                self._require_permission(session, Permission.MANAGE_ACCESS)
                group_id = first(form, "group_id")
                self.app.access.delete_group(group_id)
                self.app.audit.record(actor, "access_group.delete", "access_group", group_id)
                self._redirect("/access", message="Access group deleted.")
            elif parsed.path == "/access/credentials/save":
                self._require_permission(session, Permission.MANAGE_ACCESS)
                group_id = first(form, "group_id")
                credential_id = self.app.access.save_credential(group_id, first(form, "username"), first(form, "password"))
                self.app.audit.record(actor, "access_credential.create", "access_credential", credential_id, after={"group_id": group_id, "username": first(form, "username")})
                self._redirect("/access", message="Credential added.")
            elif parsed.path == "/access/credentials/delete":
                self._require_permission(session, Permission.MANAGE_ACCESS)
                credential_id = first(form, "credential_id")
                self.app.access.delete_credential(credential_id)
                self.app.audit.record(actor, "access_credential.delete", "access_credential", credential_id)
                self._redirect("/access", message="Credential deleted.")
            elif parsed.path == "/system/validate":
                self._require_permission(session, Permission.OPERATE_CADDY)
                self.app.caddy.validate()
                self.app.audit.record(actor, "caddy.validate", "system", "caddy")
                self._redirect("/system", message="Caddy configuration is valid.")
            elif parsed.path == "/system/reload":
                self._require_permission(session, Permission.OPERATE_CADDY)
                self.app.caddy.reload()
                self.app.audit.record(actor, "caddy.reload", "system", "caddy")
                self._redirect("/system", message="Caddy reloaded.")
            elif parsed.path == "/system/revisions/restore":
                self._require_permission(session, Permission.RESTORE_BACKUP)
                self.app.caddy.restore_revision(actor, first(form, "revision_id"))
                self._redirect("/system", message="Configuration revision restored.")
            elif parsed.path == "/system/backups/create":
                self._require_permission(session, Permission.RESTORE_BACKUP)
                path = self.app.database.backup("manual")
                self.app.audit.record(actor, "backup.create", "backup", path.name if path else "")
                self._redirect("/system", message="Backup created.")
            elif parsed.path == "/system/backups/restore":
                self._require_permission(session, Permission.RESTORE_BACKUP)
                filename = Path(first(form, "backup")).name
                self.app.database.restore(self.app.settings.backup_dir / filename)
                self.app.audit.record(actor, "backup.restore", "backup", filename)
                self._redirect("/system", message="Backup restored. Sign in again if required.")
            elif parsed.path == "/dns/save":
                self._dns_save(session, actor, form)
            elif parsed.path == "/dns/delete":
                self._dns_delete(session, actor, form)
            elif parsed.path == "/dns/ddns":
                self._ddns_save(session, actor, form)
            elif parsed.path == "/admin/users/save":
                self._require_permission(session, Permission.MANAGE_USERS)
                existing_id = first(form, "user_id")
                before = self.app.users.get(existing_id) if existing_id else None
                user_id = self.app.users.save(first(form, "username"), first(form, "display_name"), Role(first(form, "role", "viewer")), first(form, "password"), existing_id, first(form, "enabled") == "1")
                self.app.audit.record(actor, "user.update" if existing_id else "user.create", "user", user_id, before=dict(before) if before else None, after={"username": first(form, "username"), "role": first(form, "role"), "enabled": first(form, "enabled") == "1"})
                self._redirect("/admin/users", message="User saved.")
            elif parsed.path == "/admin/users/delete":
                self._require_permission(session, Permission.MANAGE_USERS)
                user_id = first(form, "user_id")
                if user_id == session["user_id"]:
                    raise ValueError("You cannot delete your current user.")
                self.app.users.delete(user_id)
                self.app.audit.record(actor, "user.delete", "user", user_id)
                self._redirect("/admin/users", message="User deleted.")
            elif parsed.path == "/admin/settings":
                self._require_permission(session, Permission.MANAGE_SETTINGS)
                self.app.database.set_setting("default_domain", first(form, "default_domain").strip().rstrip("."))
                accent = first(form, "accent", "#0f6cbd")
                if len(accent) != 7 or not accent.startswith("#") or any(character not in "0123456789abcdefABCDEF" for character in accent[1:]):
                    raise ValueError("Accent must be a six-digit hex color.")
                self.app.database.set_setting("accent", accent)
                theme = first(form, "theme", "system")
                if theme not in {"system", "light", "dark"}:
                    raise ValueError("Invalid theme.")
                with self.app.database.transaction() as connection:
                    connection.execute("UPDATE users SET theme=?,updated_at=? WHERE id=?", (theme, utc_now(), session["user_id"]))
                self.app.audit.record(actor, "settings.update", "settings", "general", after={"default_domain": first(form, "default_domain"), "theme": theme, "accent": accent})
                self._redirect("/admin/settings", message="Settings saved.")
            elif parsed.path == "/admin/notifications":
                self._require_permission(session, Permission.MANAGE_SETTINGS)
                password_env = first(form, "smtp_password_env").strip()
                if password_env and (not password_env.replace("_", "").isalnum() or password_env.upper() != password_env):
                    raise ValueError("SMTP password must reference an uppercase environment variable name.")
                value = {
                    "webhook": {"enabled": first(form, "webhook_enabled") == "1", "url": first(form, "webhook_url"), "events": form.get("webhook_events", [])},
                    "email": {"enabled": first(form, "email_enabled") == "1", "host": first(form, "smtp_host"), "port": int(first(form, "smtp_port", "25")), "starttls": first(form, "smtp_starttls") == "1", "from": first(form, "email_from"), "to": first(form, "email_to"), "username": first(form, "smtp_username"), "password_env": password_env, "events": form.get("email_events", [])},
                }
                self.app.database.set_setting("notifications", value)
                self.app.audit.record(actor, "notifications.update", "settings", "notifications", after=value)
                self._redirect("/admin/settings", message="Notification settings saved.")
            elif parsed.path == "/notifications/acknowledge":
                self._require_permission(session, Permission.VIEW)
                notification_id = int(first(form, "notification_id"))
                self.app.notifications.acknowledge(notification_id)
                self.app.audit.record(actor, "notification.acknowledge", "notification", str(notification_id))
                self._redirect("/", message="Notification acknowledged.")
            elif parsed.path == "/admin/totp/start":
                secret = new_totp_secret()
                with self.app.database.transaction() as connection:
                    connection.execute("UPDATE users SET totp_secret=?,totp_enabled=0,updated_at=? WHERE id=?", (secret, utc_now(), session["user_id"]))
                self.app.audit.record(actor, "totp.start", "user", session["user_id"])
                self._redirect("/admin/settings", message="Add the secret to your authenticator, then verify a code.")
            elif parsed.path == "/admin/totp/enable":
                user = self.app.users.get(session["user_id"])
                if not user or not verify_totp(user["totp_secret"], first(form, "code")):
                    raise ValueError("Invalid TOTP verification code.")
                with self.app.database.transaction() as connection:
                    connection.execute("UPDATE users SET totp_enabled=1,updated_at=? WHERE id=?", (utc_now(), session["user_id"]))
                self.app.audit.record(actor, "totp.enable", "user", session["user_id"])
                self._redirect("/admin/settings", message="TOTP enabled.")
            elif parsed.path == "/admin/totp/disable":
                with self.app.database.transaction() as connection:
                    connection.execute("UPDATE users SET totp_enabled=0,totp_secret='',updated_at=? WHERE id=?", (utc_now(), session["user_id"]))
                self.app.audit.record(actor, "totp.disable", "user", session["user_id"])
                self._redirect("/admin/settings", message="TOTP disabled.")
            elif parsed.path == "/admin/providers/save":
                self._provider_save(session, actor, form)
            elif parsed.path == "/admin/providers/delete":
                self._require_permission(session, Permission.MANAGE_SETTINGS)
                provider_id = first(form, "provider_id")
                self.app.providers.delete(provider_id)
                self.app.audit.record(actor, "provider.delete", "provider", provider_id)
                self._redirect("/admin/settings", message="Provider deleted.")
            else:
                self.send_error(HTTPStatus.NOT_FOUND)
        except PermissionError as exc:
            self.send_error(HTTPStatus.FORBIDDEN, str(exc))
        except Exception as exc:
            logging.exception("POST %s failed", parsed.path)
            target = self._section_path(parsed.path)
            self._redirect(target, error=str(exc))

    def _dashboard(self, session: sqlite3.Row, message: str, error: str) -> None:
        from datetime import UTC, datetime, timedelta

        routes = self.app.routes.list()
        health = route_health(routes, self.app.settings)
        access = parse_access_logs(self.app.settings.access_log_path)
        traffic = traffic_summary(access)
        cutoff = (datetime.now(UTC) - timedelta(days=30)).isoformat()
        with self.app.database.connect() as connection:
            stored = connection.execute("SELECT host,status_class,SUM(requests) requests FROM traffic_buckets WHERE bucket_start>=? GROUP BY host,status_class", (cutoff,)).fetchall()
        if stored:
            hosts: dict[str, int] = {}
            statuses: dict[str, int] = {}
            for row in stored:
                hosts[row["host"]] = hosts.get(row["host"], 0) + row["requests"]
                statuses[row["status_class"]] = statuses.get(row["status_class"], 0) + row["requests"]
            traffic = {"requests": sum(hosts.values()), "hosts": sorted(hosts.items(), key=lambda item: (-item[1], item[0]))[:8], "statuses": sorted(statuses.items())}
        self._html(views.dashboard(session, session["csrf_token"], routes, health, caddy_status(self.app.settings), certificate_files(self.app.settings.caddy_data_path), traffic, self.app.providers.list(), __version__, self.app.notifications.unacknowledged(), message, error))

    def _routes(self, session: sqlite3.Row, query: dict[str, list[str]], message: str = "", error: str = "", preview_diff: str = "", proposed_json: str = "", edit_override: ManagedRoute | None = None) -> None:
        routes = self.app.routes.list()
        edit = edit_override
        if not edit and first(query, "new") == "1":
            edit = ManagedRoute(domain=self._default_domain())
        if not edit and first(query, "edit"):
            edit = self.app.routes.get(first(query, "edit"))
            if not edit:
                raise ValueError("Route not found.")
        request_counts: dict[str, int] = {}
        for entry in parse_access_logs(self.app.settings.access_log_path, 5000):
            host = str(entry.get("host", ""))
            request_counts[host] = request_counts.get(host, 0) + 1
        self._html(views.routes_page(session, session["csrf_token"], routes, self.app.access.list_groups(), route_health(routes, self.app.settings), request_counts, edit, preview_diff, proposed_json, message, error))

    def _routes_bulk(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_ROUTES)
        ids = form.get("route_ids", [])
        action = first(form, "action")
        if not ids:
            raise ValueError("Select at least one route.")
        if action == "export":
            query = urllib.parse.urlencode({"ids": ",".join(ids)})
            self._redirect(f"/routes/export?{query}")
            return
        for route_id in ids:
            route = self.app.routes.get(route_id)
            if not route:
                continue
            if action == "enable":
                route.enabled = True
                self.app.caddy.apply(actor, f"Enable {route.name}", proposed=route)
            elif action == "disable":
                route.enabled = False
                self.app.caddy.apply(actor, f"Disable {route.name}", proposed=route)
            elif action == "duplicate":
                clone = ManagedRoute.from_json(route.to_json())
                clone.id = str(uuid.uuid4())
                clone.name = f"{route.name}-copy"
                suffix = 2
                while self.app.routes.get_by_name(clone.name):
                    clone.name = f"{route.name}-copy-{suffix}"
                    suffix += 1
                clone.created_at = ""
                clone.updated_at = ""
                self.app.caddy.apply(actor, f"Duplicate {route.name}", proposed=clone)
            elif action == "delete":
                self.app.caddy.apply(actor, f"Delete {route.name}", delete_id=route.id)
            else:
                raise ValueError("Unsupported bulk action.")
        self._redirect("/routes", message=f"Bulk action {action} completed.")

    def _routes_export(self, session: sqlite3.Row, query: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.VIEW)
        ids = set(first(query, "ids").split(",")) if first(query, "ids") else set()
        routes = [safe_route_dict(item) for item in self.app.routes.list() if not ids or item.id in ids]
        self._download("caddy-ui-routes.json", json.dumps({"version": 1, "routes": routes}, indent=2).encode("utf-8"), "application/json")

    def _routes_import(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_ROUTES)
        data = json.loads(first(form, "import_json"))
        values = data.get("routes", []) if isinstance(data, dict) else data
        for item in values:
            if item.get("redacted_custom_snippet") or any(header.get("value") == "[redacted]" for header in item.get("request_headers", []) + item.get("response_headers", [])):
                raise ValueError("Import contains redacted values. Re-enter sensitive values before importing.")
            route = ManagedRoute.from_json(item)
            if self.app.routes.get_by_name(route.name):
                raise ValueError(f"Route {route.name} already exists; import never overwrites.")
            if route.kind == RouteKind.CUSTOM:
                self._require_permission(session, Permission.MANAGE_CUSTOM_ROUTES)
            self._validate_access_group(route)
            self.app.caddy.apply(actor, f"Import {route.name}", proposed=route)
        self._redirect("/routes", message=f"Imported {len(values)} route(s).")

    def _access(self, session: sqlite3.Row, message: str, error: str) -> None:
        groups = self.app.access.list_groups()
        credentials = {group.id: self.app.access.list_credentials(group.id) for group in groups}
        self._html(views.access_page(session, session["csrf_token"], groups, credentials, message, error))

    def _logs(self, session: sqlite3.Row, query: dict[str, list[str]], message: str, error: str) -> None:
        tab = first(query, "tab", "access")
        if tab == "access":
            entries: list[Any] = parse_access_logs(self.app.settings.access_log_path, 1000)
        elif tab == "system":
            entries = list(reversed(tail_lines(self.app.settings.caddy_log_path, limit=1000)))
        elif tab == "ddns":
            result = self.app.database.setting("ddns_last_result", {})
            entries = [json.dumps(result, sort_keys=True)] if result else []
        else:
            raise ValueError("Invalid log tab.")
        self._html(views.logs_page(session, session["csrf_token"], tab, entries, message, error))

    def _logs_download(self, session: sqlite3.Row, tab: str) -> None:
        self._require_permission(session, Permission.VIEW)
        if tab == "access":
            content = "\n".join(json.dumps(item, sort_keys=True) for item in parse_access_logs(self.app.settings.access_log_path, 5000))
        elif tab == "system":
            content = "\n".join(tail_lines(self.app.settings.caddy_log_path, limit=5000))
        else:
            content = json.dumps(self.app.database.setting("ddns_last_result", {}), indent=2)
        self._download(f"caddy-ui-{tab}-logs.txt", content.encode("utf-8"), "text/plain; charset=utf-8")

    def _system(self, session: sqlite3.Row, message: str, error: str) -> None:
        revisions = self.app.caddy.list_revisions()
        backups = sorted((path.name for path in self.app.settings.backup_dir.glob("*.db")), reverse=True)
        status = caddy_status(self.app.settings)
        status["ui_version"] = __version__
        self._html(views.system_page(session, session["csrf_token"], status, certificate_files(self.app.settings.caddy_data_path), revisions, backups, message, error))

    def _diagnostics(self, session: sqlite3.Row) -> None:
        self._require_permission(session, Permission.VIEW)
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("status.json", json.dumps(caddy_status(self.app.settings), indent=2))
            safe_routes = [safe_route_dict(route) for route in self.app.routes.list()]
            archive.writestr("routes.json", json.dumps(safe_routes, indent=2))
            archive.writestr("certificates.json", json.dumps(certificate_files(self.app.settings.caddy_data_path), indent=2))
            archive.writestr("README.txt", "Secrets, sessions, password hashes, and provider credentials are intentionally excluded.\n")
        self._download("caddy-ui-diagnostics.zip", buffer.getvalue(), "application/zip")

    def _dns(self, session: sqlite3.Row, query: dict[str, list[str]], message: str, error: str) -> None:
        providers = self.app.providers.list()
        provider_id = first(query, "provider_id") or (providers[0]["id"] if providers else "")
        selected = self.app.providers.get(provider_id) if provider_id else None
        domain = first(query, "domain") or (selected.get("domains", [""])[0] if selected and selected.get("domains") else "")
        records: list[dict[str, Any]] = []
        if selected and domain:
            records = NetcupProvider(selected).records(domain)
        ddns = self.app.database.setting("ddns", {}) or {}
        ddns["interval"] = int(self.app.database.setting("ddns_interval_seconds", 300) or 300)
        self._html(views.dns_page(session, session["csrf_token"], providers, selected, domain, records, ddns, self.app.database.setting("ddns_last_result", {}) or {}, message, error))

    def _dns_save(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_DNS)
        provider_id, domain = first(form, "provider_id"), first(form, "domain")
        provider = self.app.providers.get(provider_id)
        if not provider:
            raise ValueError("Provider not found.")
        record = {"hostname": first(form, "hostname"), "type": first(form, "type"), "priority": first(form, "priority"), "destination": first(form, "destination")}
        if first(form, "id"):
            record["id"] = first(form, "id")
        NetcupProvider(provider).update(domain, [record])
        self.app.audit.record(actor, "dns.save", "dns_record", f"{domain}:{record['hostname']}:{record['type']}", after=record)
        self._redirect(f"/dns?provider_id={urllib.parse.quote(provider_id)}&domain={urllib.parse.quote(domain)}", message="DNS record saved.")

    def _dns_delete(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_DNS)
        provider_id, domain = first(form, "provider_id"), first(form, "domain")
        provider = self.app.providers.get(provider_id)
        if not provider:
            raise ValueError("Provider not found.")
        record = json.loads(first(form, "record_json"))
        NetcupProvider(provider).delete(domain, record)
        self.app.audit.record(actor, "dns.delete", "dns_record", f"{domain}:{record.get('hostname')}:{record.get('type')}", before=record)
        self._redirect(f"/dns?provider_id={urllib.parse.quote(provider_id)}&domain={urllib.parse.quote(domain)}", message="DNS record deleted.")

    def _ddns_save(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_DNS)
        provider_id = first(form, "provider_id")
        provider = self.app.providers.get(provider_id)
        domain = first(form, "domain").strip().rstrip(".")
        if not provider:
            raise ValueError("Provider not found.")
        if domain not in provider.get("domains", []):
            raise ValueError("DDNS domain must belong to the selected provider.")
        interval = max(60, min(86400, int(first(form, "interval", "300"))))
        public_ip_url = first(form, "public_ip_url", "https://api64.ipify.org").strip()
        if not public_ip_url.startswith("https://"):
            raise ValueError("Public IP lookup must use HTTPS.")
        value = {
            "enabled": first(form, "enabled") == "1",
            "provider_id": provider_id,
            "domain": domain,
            "hosts": [item.strip() for item in first(form, "hosts", "@,*").split(",") if item.strip()],
            "public_ip_url": public_ip_url,
        }
        self.app.database.set_setting("ddns", value)
        self.app.database.set_setting("ddns_interval_seconds", interval)
        self.app.audit.record(actor, "ddns.update", "settings", "ddns", after={**value, "interval": interval})
        self._redirect(f"/dns?provider_id={urllib.parse.quote(provider_id)}&domain={urllib.parse.quote(domain)}", message="DDNS settings saved.")

    def _users(self, session: sqlite3.Row, message: str, error: str) -> None:
        self._require_permission(session, Permission.MANAGE_USERS)
        self._html(views.users_page(session, session["csrf_token"], self.app.users.list(), message, error))

    def _audit(self, session: sqlite3.Row, message: str, error: str) -> None:
        self._html(views.audit_page(session, session["csrf_token"], self.app.audit.list(), message, error))

    def _settings(self, session: sqlite3.Row, message: str, error: str) -> None:
        values = {"default_domain": self._default_domain(), "accent": self.app.database.setting("accent", "#0f6cbd"), "notifications": self.app.database.setting("notifications", {})}
        self._html(views.settings_page(session, session["csrf_token"], values, self.app.providers.list(), self.app.users.get(session["user_id"]), message, error))

    def _provider_save(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_SETTINGS)
        def env_reference(value: str) -> str:
            value = value.strip()
            if value.startswith("{env.") and value.endswith("}"):
                return value
            if value.replace("_", "").isalnum() and value.upper() == value:
                return f"{{env.{value}}}"
            raise ValueError("Provider secrets must be environment variable names, not literal values.")
        provider = {
            "id": first(form, "id"), "type": "netcup", "label": first(form, "label"),
            "domains": [item.strip().rstrip(".") for item in first(form, "domains").split(",") if item.strip()],
            "customer_number": env_reference(first(form, "customer_number")),
            "api_key": env_reference(first(form, "api_key")),
            "api_password": env_reference(first(form, "api_password")),
        }
        self.app.providers.save(provider)
        self.app.audit.record(actor, "provider.save", "provider", provider["id"], after=provider)
        self._redirect("/admin/settings", message="Provider saved.")

    def _login_get(self, parsed: urllib.parse.SplitResult) -> None:
        query = urllib.parse.parse_qs(parsed.query)
        if parsed.path.startswith("/__caddy_ui_auth"):
            group = self.app.access.get_group(first(query, "group"))
            if not group:
                self.send_error(HTTPStatus.NOT_FOUND, "Access group not found.")
                return
            self._html(views.portal_login(group, first(query, "error"), first(query, "return_to", "/")))
            return
        if self.app.database.session(self._cookie(SESSION_COOKIE)):
            self._redirect("/")
            return
        self._html(views.login(first(query, "error")))

    def _login_post(self, form: dict[str, list[str]]) -> None:
        username, password, code = first(form, "username"), first(form, "password"), first(form, "totp")
        throttle_key = f"{self.client_address[0]}:{username.lower()}"
        if not self.app.throttle.allowed(throttle_key):
            self._redirect("/login", error="Too many sign-in attempts. Try again later.")
            return
        user = self.app.database.authenticate(username, password)
        if not user or (user["totp_enabled"] and not verify_totp(user["totp_secret"], code)):
            self.app.throttle.record_failure(throttle_key)
            self.app.audit.record(Actor(username=username or "unknown", remote_address=self.client_address[0]), "login.failed", "session", "", result="failed")
            self._redirect("/login", error="Invalid username, password, or TOTP code.")
            return
        self.app.throttle.clear(throttle_key)
        token, _ = self.app.database.create_session(user["id"], self.app.settings.session_ttl_seconds, self.client_address[0], self.headers.get("User-Agent", ""))
        self.app.audit.record(Actor(user["id"], user["username"], self.client_address[0]), "login.success", "session", token_hash(token)[:12])
        self._redirect("/", set_session=token)

    def _portal_authorize(self, parsed: urllib.parse.SplitResult) -> None:
        query = urllib.parse.parse_qs(parsed.query)
        group_id = first(query, "group")
        token = self._cookie(PORTAL_COOKIE_PREFIX + group_id)
        with self.app.database.connect() as connection:
            row = connection.execute(
                """SELECT access_credentials.username FROM portal_sessions
                   JOIN access_credentials ON access_credentials.id=portal_sessions.credential_id
                   WHERE portal_sessions.token_hash=? AND portal_sessions.group_id=? AND portal_sessions.expires_at>? AND access_credentials.enabled=1""",
                (token_hash(token), group_id, utc_now()),
            ).fetchone() if token else None
        if row:
            self.send_response(HTTPStatus.OK)
            self.send_header("Remote-User", row["username"])
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        original = self.headers.get("X-Forwarded-Uri", "/")
        location = f"/__caddy_ui_auth/login?{urllib.parse.urlencode({'group': group_id, 'return_to': original})}"
        self.send_response(HTTPStatus.FOUND)
        self.send_header("Location", location)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def _portal_login_post(self, form: dict[str, list[str]]) -> None:
        group_id = first(form, "group")
        group = self.app.access.get_group(group_id)
        if not group:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        throttle_key = f"portal:{self.client_address[0]}:{group_id}:{first(form, 'username').lower()}"
        if not self.app.throttle.allowed(throttle_key):
            self._redirect(f"/__caddy_ui_auth/login?{urllib.parse.urlencode({'group': group_id, 'error': 'Too many sign-in attempts. Try again later.'})}")
            return
        credential = self.app.access.authenticate(group_id, first(form, "username"), first(form, "password"))
        return_to = first(form, "return_to", "/")
        if not return_to.startswith("/") or return_to.startswith("//"):
            return_to = "/"
        if not credential:
            self.app.throttle.record_failure(throttle_key)
            self.app.audit.record(Actor(username=first(form, "username") or "unknown", remote_address=self.client_address[0]), "portal_login.failed", "access_group", group_id, result="failed")
            location = f"/__caddy_ui_auth/login?{urllib.parse.urlencode({'group': group_id, 'return_to': return_to, 'error': 'Invalid username or password.'})}"
            self._redirect(location)
            return
        self.app.throttle.clear(throttle_key)
        from datetime import UTC, datetime, timedelta
        token, hashed, _ = new_session_tokens()
        with self.app.database.transaction() as connection:
            connection.execute(
                "INSERT INTO portal_sessions(token_hash,credential_id,group_id,expires_at) VALUES(?,?,?,?)",
                (hashed, credential["id"], group_id, (datetime.now(UTC) + timedelta(hours=12)).isoformat()),
            )
        self.app.audit.record(Actor(username=credential["username"], remote_address=self.client_address[0]), "portal_login.success", "access_group", group_id)
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", return_to)
        self.send_header("Set-Cookie", self._cookie_header(PORTAL_COOKIE_PREFIX + group_id, token, 43200))
        self.send_header("Content-Length", "0")
        self.end_headers()

    def _require_session(self, api: bool) -> sqlite3.Row | None:
        session = self.app.database.session(self._cookie(SESSION_COOKIE))
        if session:
            return session
        if api:
            self._json({"error": "authentication required"}, HTTPStatus.UNAUTHORIZED)
        else:
            self._redirect("/login")
        return None

    def _require_permission(self, session: sqlite3.Row, permission: Permission) -> None:
        if not self.app.database.permitted(session, permission):
            raise PermissionError(f"Permission required: {permission.value}")

    @staticmethod
    def _is_admin(session: sqlite3.Row) -> bool:
        return session["role"] == Role.ADMIN.value

    def _default_domain(self) -> str:
        return str(self.app.database.setting("default_domain", self.app.settings.default_domain) or "")

    def _validate_access_group(self, route: ManagedRoute) -> None:
        if route.access_group_id and not self.app.access.get_group(route.access_group_id):
            raise ValueError("Selected access group does not exist.")

    def _store_route_preview(self, session: sqlite3.Row, route: ManagedRoute) -> str:
        from datetime import UTC, datetime, timedelta

        preview_id = secrets.token_urlsafe(24)
        now = datetime.now(UTC)
        with self.app.database.transaction() as connection:
            connection.execute("DELETE FROM route_previews WHERE expires_at<?", (now.isoformat(),))
            connection.execute(
                "INSERT INTO route_previews(id,user_id,route_json,created_at,expires_at) VALUES(?,?,?,?,?)",
                (preview_id, session["user_id"], route.to_json(), now.isoformat(), (now + timedelta(minutes=10)).isoformat()),
            )
        return preview_id

    def _consume_route_preview(self, session: sqlite3.Row, preview_id: str) -> ManagedRoute:
        with self.app.database.transaction() as connection:
            row = connection.execute(
                "SELECT route_json FROM route_previews WHERE id=? AND user_id=? AND expires_at>?",
                (preview_id, session["user_id"], utc_now()),
            ).fetchone()
            connection.execute("DELETE FROM route_previews WHERE id=?", (preview_id,))
        if not row:
            raise ValueError("Route preview is missing or expired. Create a new preview.")
        return ManagedRoute.from_json(row["route_json"])

    def _form(self) -> dict[str, list[str]]:
        length = max(0, int(self.headers.get("Content-Length", "0")))
        if length > 2 * 1024 * 1024:
            raise ValueError("Request body exceeds the 2 MiB limit.")
        body = self.rfile.read(length).decode("utf-8")
        return urllib.parse.parse_qs(body, keep_blank_values=True)

    def _cookie(self, name: str) -> str:
        cookie = SimpleCookie(self.headers.get("Cookie", ""))
        return cookie[name].value if name in cookie else ""

    def _cookie_header(self, name: str, value: str, max_age: int) -> str:
        secure = "; Secure" if self.app.settings.secure_cookies else ""
        return f"{name}={value}; Path=/; HttpOnly; SameSite=Strict; Max-Age={max_age}{secure}"

    def _static(self, path: str) -> None:
        filename = Path(path).name
        if filename not in {"app.css", "app.js"}:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        content = (STATIC_DIR / filename).read_bytes()
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/css; charset=utf-8" if filename.endswith(".css") else "text/javascript; charset=utf-8")
        self.send_header("Cache-Control", "public, max-age=3600")
        self.send_header("Content-Length", str(len(content)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)

    def _html(self, content: bytes, status: HTTPStatus = HTTPStatus.OK) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)

    def _json(self, value: Any, status: HTTPStatus = HTTPStatus.OK) -> None:
        content = json.dumps(value, separators=(",", ":"), default=str).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)

    def _download(self, filename: str, content: bytes, content_type: str) -> None:
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Disposition", f'attachment; filename="{filename}"')
        self.send_header("Content-Length", str(len(content)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)

    def _redirect(self, path: str, message: str = "", error: str = "", set_session: str = "", clear_session: bool = False) -> None:
        if message or error:
            separator = "&" if "?" in path else "?"
            path += separator + urllib.parse.urlencode({"message": message, "error": error})
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", path)
        if set_session:
            self.send_header("Set-Cookie", self._cookie_header(SESSION_COOKIE, set_session, self.app.settings.session_ttl_seconds))
        if clear_session:
            self.send_header("Set-Cookie", self._cookie_header(SESSION_COOKIE, "", 0))
        self.send_header("Content-Length", "0")
        self._security_headers()
        self.end_headers()

    def _security_headers(self) -> None:
        self.send_header("Content-Security-Policy", "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; form-action 'self'; frame-ancestors 'none'; base-uri 'none'")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Permissions-Policy", "camera=(), microphone=(), geolocation=()")

    @staticmethod
    def _section_path(path: str) -> str:
        if path.startswith("/routes"): return "/routes"
        if path.startswith("/access"): return "/access"
        if path.startswith("/system"): return "/system"
        if path.startswith("/dns"): return "/dns"
        if path.startswith("/admin/users"): return "/admin/users"
        if path.startswith("/admin"): return "/admin/settings"
        return "/"

    def log_message(self, fmt: str, *args: Any) -> None:
        logging.info("%s - %s", self.client_address[0], fmt % args)


def create_handler(application: Application) -> type[Handler]:
    class BoundHandler(Handler):
        app = application
    return BoundHandler


def main() -> int:
    logging.basicConfig(level=os.getenv("LOG_LEVEL", "INFO").upper(), format="%(asctime)s %(levelname)s %(message)s")
    settings = Settings.from_environment()
    application = Application(settings)
    application.start_jobs()
    server = ThreadingHTTPServer((settings.host, settings.port), create_handler(application))
    logging.info("Caddy UI listening on %s:%s", settings.host, settings.port)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        application.jobs.stop()
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
