from __future__ import annotations

import json
import logging
import os
import secrets
import sqlite3
import sys
import time
import urllib.parse
from datetime import UTC, datetime, timedelta
from http import HTTPStatus
from http.server import ThreadingHTTPServer
from typing import Any

from . import __version__, analytics_views, security_views, views
from .alerts import ExtendedNotificationService
from .analytics import AnalyticsFilters, AnalyticsRepository, analytics_settings
from .audit import Actor
from .config import Settings
from .domain import Permission, Role
from .jobs import JobRunner
from .monitoring import caddy_status, certificate_files, parse_access_logs, route_health, traffic_summary
from .observability import ObservabilityJobRunner
from .protection import PROTECTION_LEVELS, SecurityCaddyManager, SecurityService, protection_settings
from .security import new_session_tokens, token_hash, verify_totp
from .web import Application as BaseApplication
from .web import Handler as BaseHandler
from .web import PORTAL_COOKIE_PREFIX, SESSION_COOKIE, create_handler as base_create_handler, first


class Application(BaseApplication):
    def __init__(self, settings: Settings):
        super().__init__(settings)
        self.notifications = ExtendedNotificationService(self.database)
        self.analytics = AnalyticsRepository(self.database)
        self.security = SecurityService(self.database, settings.caddyfile_path, self.notifications)
        self.security.security_log_path = settings.access_log_path.with_name("security.log")
        self.caddy = SecurityCaddyManager(settings, self.database, self.audit)
        self.jobs = JobRunner(settings, self.database, self.notifications)
        self.observability_jobs = ObservabilityJobRunner(settings, self.analytics, self.security)

    def start_jobs(self) -> None:
        self._activate_security_policy()
        self.jobs.start()
        self.observability_jobs.start()

    def stop_jobs(self) -> None:
        self.observability_jobs.stop()
        self.jobs.stop()

    def _activate_security_policy(self) -> None:
        if protection_settings(self.database)["level"] == "off" or not self.routes.list():
            return
        try:
            self.caddy.apply_security_configuration(Actor(username="system", remote_address="local"))
        except Exception as exc:
            logging.warning("Security policy could not be activated: %s", exc)
            existing = self.database.setting("security_activation_warning", {}) or {}
            message = str(exc)
            if existing.get("message") != message:
                self.notifications.create(
                    "warning",
                    "security.activation.failed",
                    "Security protection could not be activated",
                    "The active Caddy build did not accept the protection configuration. Existing routes were restored. " + message,
                    "system",
                    "security",
                )
                self.database.set_setting("security_activation_warning", {"message": message, "at": datetime.now(UTC).isoformat()})


class Handler(BaseHandler):
    app: Application

    def do_GET(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        if parsed.path in {"/analytics", "/analytics/client", "/security", "/logs/export", "/api/analytics", "/api/live/logs"}:
            session = self._require_session(api=parsed.path.startswith("/api/"))
            if not session:
                return
            query = urllib.parse.parse_qs(parsed.query)
            try:
                if parsed.path == "/analytics":
                    self._analytics(session, query)
                elif parsed.path == "/analytics/client":
                    self._analytics_client(session, query)
                elif parsed.path == "/security":
                    self._security(session, query)
                elif parsed.path == "/logs/export":
                    self._logs_export(session, query)
                elif parsed.path == "/api/analytics":
                    self._analytics_api(session, query)
                else:
                    self._live_logs(session, query)
            except PermissionError as exc:
                self.send_error(HTTPStatus.FORBIDDEN, str(exc))
            except Exception as exc:
                logging.exception("GET %s failed", parsed.path)
                if parsed.path.startswith("/api/"):
                    self._json({"error": str(exc)}, HTTPStatus.BAD_REQUEST)
                else:
                    self._redirect("/", error=str(exc))
            return
        super().do_GET()

    def do_POST(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        enhanced_paths = {
            "/saved-views/save",
            "/saved-views/delete",
            "/analytics/settings",
            "/alerts/settings",
            "/security/settings",
            "/security/route-limit",
            "/security/ban",
            "/security/unban",
        }
        if parsed.path not in enhanced_paths:
            super().do_POST()
            return
        try:
            form = self._form()
        except (ValueError, UnicodeDecodeError) as exc:
            self.send_error(HTTPStatus.BAD_REQUEST, str(exc))
            return
        session = self._require_session(api=False)
        if not session:
            return
        if not secrets.compare_digest(first(form, "csrf"), str(session["csrf_token"])):
            self.send_error(HTTPStatus.FORBIDDEN, "Invalid CSRF token.")
            return
        actor = Actor.from_session(session, self._client_ip())
        try:
            if parsed.path == "/saved-views/save":
                self._save_view(session, form)
            elif parsed.path == "/saved-views/delete":
                self.app.analytics.delete_view(session["user_id"], int(first(form, "view_id")))
                self._redirect("/logs", message="Saved view deleted.")
            elif parsed.path == "/analytics/settings":
                self._analytics_settings_save(session, actor, form)
            elif parsed.path == "/alerts/settings":
                self._alerts_settings_save(session, actor, form)
            elif parsed.path == "/security/settings":
                self._security_settings_save(session, actor, form)
            elif parsed.path == "/security/route-limit":
                self._route_limit_save(session, actor, form)
            elif parsed.path == "/security/ban":
                self._require_permission(session, Permission.MANAGE_SETTINGS)
                seconds = max(60, min(int(first(form, "duration", "86400")), 31_536_000))
                self.app.security.ban_ip(first(form, "ip"), first(form, "reason") or "Manual administrator block", "manual", seconds)
                self.app.audit.record(actor, "security.ip_block", "ip", first(form, "ip"), after={"duration_seconds": seconds, "reason": first(form, "reason")})
                self._redirect("/security?tab=blocked", message="Temporary IP block added.")
            elif parsed.path == "/security/unban":
                self._require_permission(session, Permission.MANAGE_SETTINGS)
                ip = first(form, "ip")
                self.app.security.unban_ip(ip)
                self.app.audit.record(actor, "security.ip_unblock", "ip", ip)
                self._redirect("/security?tab=blocked", message="IP block removed.")
        except PermissionError as exc:
            self.send_error(HTTPStatus.FORBIDDEN, str(exc))
        except Exception as exc:
            logging.exception("POST %s failed", parsed.path)
            target = "/security" if parsed.path.startswith("/security/") else "/admin/settings"
            self._redirect(target, error=str(exc))

    def _html(self, content: bytes, status: HTTPStatus = HTTPStatus.OK) -> None:
        value = content.decode("utf-8")
        value = self._inject_navigation(value)
        super()._html(value.encode("utf-8"), status)

    def _inject_navigation(self, html_value: str) -> str:
        if 'href="/analytics"' in html_value:
            return html_value
        marker = 'href="/access"'
        position = html_value.find(marker)
        if position < 0:
            return html_value
        end = html_value.find("</a>", position)
        if end < 0:
            return html_value
        end += 4
        path = urllib.parse.urlsplit(self.path).path
        analytics_class = "active" if path.startswith("/analytics") else ""
        security_class = "active" if path.startswith("/security") else ""
        addition = (
            f'\n       <a class="{analytics_class}" href="/analytics">{views.icon("dashboard")}Analytics</a>'
            f'\n       <a class="{security_class}" href="/security">{views.icon("admin")}Security</a>'
        )
        return html_value[:end] + addition + html_value[end:]

    def _settings(self, session: sqlite3.Row, message: str, error: str) -> None:
        values = {
            "default_domain": self._default_domain(),
            "accent": self.app.database.setting("accent", "#0f6cbd"),
            "notifications": self.app.database.setting("notifications", {}),
        }
        page = views.settings_page(session, session["csrf_token"], values, self.app.providers.list(), self.app.users.get(session["user_id"]), message, error).decode("utf-8")
        extension = security_views.settings_extension(
            session["csrf_token"],
            analytics_settings(self.app.database),
            values["notifications"] or {},
            self._is_admin(session),
        )
        page = page.replace("</main>", extension + "</main>", 1)
        self._html(page.encode("utf-8"))

    def _dashboard(self, session: sqlite3.Row, message: str, error: str) -> None:
        routes = self.app.routes.list()
        health = route_health(routes, self.app.settings)
        access = parse_access_logs(self.app.settings.access_log_path)
        traffic = traffic_summary(access)
        cutoff = (datetime.now(UTC) - timedelta(days=30)).isoformat()
        with self.app.database.connect() as connection:
            stored = connection.execute("SELECT host,status_class,SUM(requests) requests FROM traffic_buckets WHERE bucket_start>=? GROUP BY host,status_class", (cutoff,)).fetchall()
        if stored:
            hosts: dict[str, int] = {}; statuses: dict[str, int] = {}
            for row in stored:
                hosts[row["host"]] = hosts.get(row["host"], 0) + row["requests"]
                statuses[row["status_class"]] = statuses.get(row["status_class"], 0) + row["requests"]
            traffic = {"requests": sum(hosts.values()), "hosts": sorted(hosts.items(), key=lambda item: (-item[1], item[0]))[:8], "statuses": sorted(statuses.items())}
        page = views.dashboard(session, session["csrf_token"], routes, health, caddy_status(self.app.settings), certificate_files(self.app.settings.caddy_data_path), traffic, self.app.providers.list(), __version__, self.app.notifications.unacknowledged(), message, error).decode("utf-8")
        start, end = self.app.analytics.resolve_range("24h")
        summary = self.app.analytics.summary(AnalyticsFilters(client_type="human"), start, end)
        strip = (
            '<div class="grid dashboard-analytics">'
            + analytics_views.metric("Requests 24h", f"{int(summary.get('requests', 0)):,}", "Human traffic", "/analytics?range=24h&client=human")
            + analytics_views.metric("P95 24h", analytics_views.format_ms(summary.get("p95_ms")), "Response time", "/analytics?tab=performance&range=24h&client=human")
            + analytics_views.metric("5xx 24h", f"{int(summary.get('errors_5xx', 0)):,}", "Server errors", "/logs?range=24h&status=5xx")
            + analytics_views.metric("Security", f"{int(self.app.security.summary().get('active_bans', 0))}", "Active IP blocks", "/security?tab=blocked")
            + '</div>'
        )
        page = page.replace('<div class="grid">', strip + '<div class="grid">', 1)
        self._html(page.encode("utf-8"))

    def _logs(self, session: sqlite3.Row, query: dict[str, list[str]], message: str, error: str) -> None:
        tab = first(query, "tab", "access")
        if tab not in {"", "access"}:
            super()._logs(session, query, message, error)
            return
        range_name = first(query, "range", "24h")
        filters = AnalyticsFilters.from_query(query)
        if first(query, "errors") == "1" and not filters.status:
            filters.status = "errors"
        start, end = self.app.analytics.resolve_range(range_name, first(query, "start"), first(query, "end"))
        page = max(1, int(first(query, "page", "1") or 1))
        rows = self.app.analytics.events(filters, start, end, 200, (page - 1) * 200)
        total = self.app.analytics.count_events(filters, start, end)
        dimensions = self.app.analytics.dimensions(max(start, datetime.now(UTC) - timedelta(days=30)), end)
        self._html(analytics_views.logs_page(session, session["csrf_token"], rows, filters, range_name, dimensions, total, page, self.app.analytics.saved_views(session["user_id"], "logs"), self._is_admin(session), message, error))

    def _analytics(self, session: sqlite3.Row, query: dict[str, list[str]]) -> None:
        tab = first(query, "tab", "overview")
        if tab not in {"overview", "performance", "traffic", "endpoints", "clients"}:
            tab = "overview"
        range_name = first(query, "range", "24h")
        filters = AnalyticsFilters.from_query(query)
        start, end = self.app.analytics.resolve_range(range_name, first(query, "start"), first(query, "end"))
        summary = self.app.analytics.summary(filters, start, end)
        series = self.app.analytics.series(filters, start, end)
        top_hosts = self.app.analytics.top("host", filters, start, end)
        top_endpoints = self.app.analytics.top("endpoint", filters, start, end)
        slow = self.app.analytics.slow_endpoints(filters, max(start, datetime.now(UTC) - timedelta(days=30)), end)
        clients = self.app.analytics.top("remote_ip", filters, max(start, datetime.now(UTC) - timedelta(days=30)), end)
        dimensions = self.app.analytics.dimensions(max(start, datetime.now(UTC) - timedelta(days=30)), end)
        self._html(analytics_views.analytics_page(session, session["csrf_token"], tab, range_name, filters, summary, series, top_hosts, top_endpoints, slow, clients, dimensions, self.app.analytics.saved_views(session["user_id"], "analytics")))

    def _analytics_client(self, session: sqlite3.Row, query: dict[str, list[str]]) -> None:
        ip = first(query, "ip").strip()
        if not ip:
            raise ValueError("Client IP is required.")
        range_name = first(query, "range", "30d")
        start, end = self.app.analytics.resolve_range(range_name)
        detail = self.app.analytics.client_detail(ip, max(start, datetime.now(UTC) - timedelta(days=30)), end)
        bans = {row["ip"] for row in self.app.security.active_bans()}
        self._html(analytics_views.client_page(session, session["csrf_token"], detail, range_name, self.app.security.events(100, ip), ip in bans, self._is_admin(session)))

    def _security(self, session: sqlite3.Row, query: dict[str, list[str]]) -> None:
        tab = first(query, "tab", "overview")
        if tab not in {"overview", "threats", "blocked", "limits", "login"}:
            tab = "overview"
        self._html(security_views.security_page(session, session["csrf_token"], tab, self.app.security.summary(), self.app.security.events(250), self.app.security.active_bans(), protection_settings(self.app.database), self.app.routes.list(), self._is_admin(session)))

    def _analytics_api(self, session: sqlite3.Row, query: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.VIEW)
        range_name = first(query, "range", "24h")
        filters = AnalyticsFilters.from_query(query)
        start, end = self.app.analytics.resolve_range(range_name, first(query, "start"), first(query, "end"))
        self._json({"summary": self.app.analytics.summary(filters, start, end), "series": self.app.analytics.series(filters, start, end)})

    def _logs_export(self, session: sqlite3.Row, query: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_SETTINGS)
        range_name = first(query, "range", "24h")
        filters = AnalyticsFilters.from_query(query)
        start, end = self.app.analytics.resolve_range(range_name, first(query, "start"), first(query, "end"))
        format_name = first(query, "format", "csv").lower()
        if format_name not in {"csv", "json"}:
            raise ValueError("Export format must be CSV or JSON.")
        content, content_type = self.app.analytics.export(filters, start, end, format_name)
        self._download(f"caddy-ui-requests.{format_name}", content, content_type)

    def _live_logs(self, session: sqlite3.Row, query: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.VIEW)
        filters = AnalyticsFilters.from_query(query)
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache, no-store")
        self.send_header("Connection", "keep-alive")
        self._security_headers()
        self.end_headers()
        last = first(query, "since")
        deadline = time.time() + 25
        try:
            while time.time() < deadline:
                end = datetime.now(UTC)
                start = end - timedelta(minutes=15)
                rows = list(reversed(self.app.analytics.events(filters, start, end, 100)))
                fresh = [row for row in rows if not last or row["occurred_at"] > last]
                for row in fresh:
                    payload = {key: row[key] for key in ("occurred_at", "host", "method", "uri", "endpoint", "status", "bytes_sent", "duration_ms", "remote_ip", "user_agent", "client_type", "category")}
                    self.wfile.write(f"data: {json.dumps(payload, separators=(',', ':'))}\n\n".encode("utf-8"))
                    last = str(row["occurred_at"])
                self.wfile.write(b": keepalive\n\n")
                self.wfile.flush()
                time.sleep(2)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def _save_view(self, session: sqlite3.Row, form: dict[str, list[str]]) -> None:
        kind = first(form, "kind")
        payload = json.loads(first(form, "query", "{}"))
        if not isinstance(payload, dict):
            raise ValueError("Saved view query must be an object.")
        clean = {str(key): str(value) for key, value in payload.items() if str(value)}
        self.app.analytics.save_view(session["user_id"], kind, first(form, "name"), clean)
        target = "/analytics" if kind == "analytics" else "/logs"
        self._redirect(target + ("?" + urllib.parse.urlencode(clean) if clean else ""), message="Saved view created.")

    def _analytics_settings_save(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_SETTINGS)
        normal = max(1, int(first(form, "normal_ms", "500")))
        warning = max(normal, int(first(form, "warning_ms", "1000")))
        slow = max(warning, int(first(form, "slow_ms", "3000")))
        value = {
            "raw_retention_days": max(30, min(365, int(first(form, "raw_retention_days", "30")))),
            "aggregate_retention_days": max(30, min(3650, int(first(form, "aggregate_retention_days", "365")))),
            "performance": {"normal_ms": normal, "warning_ms": warning, "slow_ms": slow},
            "redacted_query_names": sorted({item.strip().lower() for item in first(form, "redacted_query_names").split(",") if item.strip()}),
        }
        before = self.app.database.setting("analytics", {}) or {}
        self.app.database.set_setting("analytics", value)
        self.app.audit.record(actor, "analytics.settings.update", "settings", "analytics", before=before, after=value)
        self._redirect("/admin/settings", message="Analytics settings saved.")

    def _alerts_settings_save(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_SETTINGS)
        def env_name(name: str) -> str:
            value = first(form, name).strip()
            if value and (not value.replace("_", "").isalnum() or value.upper() != value):
                raise ValueError(f"{name} must be an uppercase environment variable name.")
            return value
        before = self.app.database.setting("notifications", {}) or {}
        value = dict(before)
        value["discord"] = {"enabled": first(form, "discord_enabled") == "1", "webhook_env": env_name("discord_webhook_env"), "events": [item.strip() for item in first(form, "discord_events", "security.threat").split(",") if item.strip()]}
        value["telegram"] = {"enabled": first(form, "telegram_enabled") == "1", "token_env": env_name("telegram_token_env"), "chat_id": first(form, "telegram_chat_id").strip(), "events": [item.strip() for item in first(form, "telegram_events", "security.threat").split(",") if item.strip()]}
        self.app.database.set_setting("notifications", value)
        self.app.audit.record(actor, "notifications.extended.update", "settings", "notifications", before={"discord": before.get("discord", {}), "telegram": before.get("telegram", {})}, after={"discord": value["discord"], "telegram": value["telegram"]})
        self._redirect("/admin/settings", message="Alert channels saved.")

    def _security_settings_save(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_SETTINGS)
        level = first(form, "level", "balanced").lower()
        if level not in PROTECTION_LEVELS:
            raise ValueError("Invalid protection level.")
        before = self.app.database.setting("protection", {}) or {}
        value = {
            "level": level,
            "global": {
                "requests": max(1, int(first(form, "requests", "300"))),
                "window_seconds": max(1, int(first(form, "window_seconds", "60"))),
                "burst": max(0, int(first(form, "burst", "60"))),
                "block_seconds": max(60, min(86400, int(first(form, "block_seconds", "900")))),
            },
            "login": {
                "delay_after": max(1, int(first(form, "login_delay_after", "5"))),
                "block_after": max(2, int(first(form, "login_block_after", "10"))),
                "window_seconds": 900,
            },
            "trusted_proxies": [item.strip() for item in first(form, "trusted_proxies").splitlines() if item.strip()],
            "allowlist": [item.strip() for item in first(form, "allowlist").splitlines() if item.strip()],
            "route_overrides": before.get("route_overrides", {}) if isinstance(before, dict) else {},
        }
        self.app.database.set_setting("protection", value)
        try:
            self.app.caddy.apply_security_configuration(actor)
        except Exception:
            self.app.database.set_setting("protection", before)
            try:
                self.app.caddy.apply_security_configuration(actor)
            except Exception:
                logging.exception("Failed to restore previous security configuration")
            raise
        self.app.audit.record(actor, "security.settings.update", "settings", "protection", before=before, after=value)
        self._redirect("/security?tab=limits", message="Protection settings applied.")

    def _route_limit_save(self, session: sqlite3.Row, actor: Actor, form: dict[str, list[str]]) -> None:
        self._require_permission(session, Permission.MANAGE_SETTINGS)
        route_id = first(form, "route_id")
        if not self.app.routes.get(route_id):
            raise ValueError("Route not found.")
        mode = first(form, "mode", "inherit")
        if mode not in {"inherit", "off", "custom"}:
            raise ValueError("Invalid route protection mode.")
        before = self.app.database.setting("protection", {}) or {}
        value = json.loads(json.dumps(before)) if before else {"level": "balanced"}
        overrides = value.setdefault("route_overrides", {})
        if mode == "inherit":
            overrides.pop(route_id, None)
        elif mode == "off":
            overrides[route_id] = {"mode": "off"}
        else:
            overrides[route_id] = {"mode": "custom", "requests": max(1, int(first(form, "requests", "300"))), "window_seconds": max(1, int(first(form, "window_seconds", "60"))), "burst": max(0, int(first(form, "burst", "60")))}
        self.app.database.set_setting("protection", value)
        try:
            self.app.caddy.apply_security_configuration(actor)
        except Exception:
            self.app.database.set_setting("protection", before)
            try:
                self.app.caddy.apply_security_configuration(actor)
            except Exception:
                logging.exception("Failed to restore prior route protection")
            raise
        self.app.audit.record(actor, "security.route_limit.update", "route", route_id, before=before.get("route_overrides", {}).get(route_id) if isinstance(before, dict) else None, after=overrides.get(route_id))
        self._redirect("/security?tab=limits", message="Route protection saved.")

    def _client_ip(self) -> str:
        return self.app.security.client_ip(self.client_address[0], self.headers)

    def _login_post(self, form: dict[str, list[str]]) -> None:
        username, password, code = first(form, "username"), first(form, "password"), first(form, "totp")
        client_ip = self._client_ip()
        state = self.app.security.login_state("ui", client_ip, username)
        if not state["allowed"]:
            self._rate_limited_login(state["retry_after"], views.login("Too many sign-in attempts. Try again later."))
            return
        if state["delay"]:
            time.sleep(min(float(state["delay"]), 2.0))
        user = self.app.database.authenticate(username, password)
        if not user or (user["totp_enabled"] and not verify_totp(user["totp_secret"], code)):
            self.app.security.record_login_failure("ui", client_ip, username, self.headers.get("Host", ""))
            self.app.audit.record(Actor(username=username or "unknown", remote_address=client_ip), "login.failed", "session", "", result="failed")
            self._redirect("/login", error="Invalid username, password, or TOTP code.")
            return
        self.app.security.clear_login("ui", client_ip, username)
        token, _ = self.app.database.create_session(user["id"], self.app.settings.session_ttl_seconds, client_ip, self.headers.get("User-Agent", ""))
        self.app.audit.record(Actor(user["id"], user["username"], client_ip), "login.success", "session", token_hash(token)[:12])
        self._redirect("/", set_session=token)

    def _portal_login_post(self, form: dict[str, list[str]]) -> None:
        group_id = first(form, "group")
        group = self.app.access.get_group(group_id)
        if not group:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        username = first(form, "username")
        client_ip = self._client_ip()
        state = self.app.security.login_state("portal:" + group_id, client_ip, username)
        return_to = first(form, "return_to", "/")
        if not return_to.startswith("/") or return_to.startswith("//"):
            return_to = "/"
        if not state["allowed"]:
            location = f"/__caddy_ui_auth/login?{urllib.parse.urlencode({'group': group_id, 'return_to': return_to, 'error': 'Too many sign-in attempts. Try again later.'})}"
            self._redirect(location)
            return
        if state["delay"]:
            time.sleep(min(float(state["delay"]), 2.0))
        credential = self.app.access.authenticate(group_id, username, first(form, "password"))
        if not credential:
            self.app.security.record_login_failure("portal:" + group_id, client_ip, username, self.headers.get("Host", ""))
            self.app.audit.record(Actor(username=username or "unknown", remote_address=client_ip), "portal_login.failed", "access_group", group_id, result="failed")
            location = f"/__caddy_ui_auth/login?{urllib.parse.urlencode({'group': group_id, 'return_to': return_to, 'error': 'Invalid username or password.'})}"
            self._redirect(location)
            return
        self.app.security.clear_login("portal:" + group_id, client_ip, username)
        token, hashed, _ = new_session_tokens()
        with self.app.database.transaction() as connection:
            connection.execute("INSERT INTO portal_sessions(token_hash,credential_id,group_id,expires_at) VALUES(?,?,?,?)", (hashed, credential["id"], group_id, (datetime.now(UTC) + timedelta(hours=12)).isoformat()))
        self.app.audit.record(Actor(username=credential["username"], remote_address=client_ip), "portal_login.success", "access_group", group_id)
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", return_to)
        self.send_header("Set-Cookie", self._cookie_header(PORTAL_COOKIE_PREFIX + group_id, token, 43200))
        self.send_header("Content-Length", "0")
        self._security_headers()
        self.end_headers()

    def _rate_limited_login(self, retry_after: int, content: bytes) -> None:
        self.send_response(HTTPStatus.TOO_MANY_REQUESTS)
        self.send_header("Retry-After", str(max(1, retry_after)))
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)

    @staticmethod
    def _section_path(path: str) -> str:
        if path.startswith("/analytics"): return "/analytics"
        if path.startswith("/security"): return "/security"
        return BaseHandler._section_path(path)


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
        application.stop_jobs()
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
