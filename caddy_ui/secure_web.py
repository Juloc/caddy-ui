from __future__ import annotations

import json
import logging
import os
import secrets
import sqlite3
import urllib.parse
from datetime import UTC, datetime, timedelta
from http import HTTPStatus
from http.server import ThreadingHTTPServer
from typing import Any

from . import __version__, secure_views
from .audit import Actor, AuditLog
from .config import Settings
from .db import Database, utc_now
from .jobs import JobRunner
from .migration import import_legacy
from .notifications import NotificationService
from .providers.netcup import NetcupProvider
from .repositories import AccessRepository, ProviderRepository, RouteRepository, UserRepository
from .secure_caddy import HardenedCaddyManager
from .security import token_hash, verify_totp
from .security_policy import (
    INTERNAL_SECRET_HEADER,
    PersistentLoginThrottle,
    REFERENCE_RE,
    SecurityPolicy,
    normalize_host,
    safe_return_path,
)
from .web import Handler as BaseHandler
from .web import first


SESSION_COOKIE = "caddy_ui_session"
PORTAL_COOKIE_PREFIX = "caddy_portal_"
LOGIN_CSRF_COOKIE = "caddy_ui_login_csrf"


class SecureApplication:
    def __init__(self, settings: Settings):
        self.settings = settings
        self.security = SecurityPolicy.from_environment()
        self.database = Database(settings)
        self.database.initialize()
        self.audit = AuditLog(self.database)
        self.routes = RouteRepository(self.database)
        self.access = AccessRepository(self.database)
        self.users = UserRepository(self.database)
        self.providers = ProviderRepository(self.database)
        self.caddy = HardenedCaddyManager(settings, self.database, self.audit, self.security)
        self.notifications = NotificationService(self.database)
        self.throttle = PersistentLoginThrottle(self.database, self.security)
        import_legacy(settings, self.database, self.audit)
        self.caddy.migrate_legacy_layout()
        # Validate existing managed routes at startup. Protected routes must never run without the shared secret.
        self.caddy.rendered()
        self.jobs = JobRunner(settings, self.database, self.notifications)

    def start_jobs(self) -> None:
        self.jobs.start()


class SecureHandler(BaseHandler):
    server_version = "CaddyUI"
    sys_version = ""
    app: SecureApplication

    def do_GET(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        if parsed.path != "/api/health" and not self._enforce_proxy_boundary(parsed.path):
            return
        super().do_GET()

    def do_POST(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        if not self._enforce_proxy_boundary(parsed.path):
            return
        if not self._origin_allowed(parsed.path):
            self.send_error(HTTPStatus.FORBIDDEN, "Cross-origin request rejected.")
            return
        super().do_POST()

    def _is_portal_path(self, path: str) -> bool:
        return path.startswith("/__caddy_ui_auth") or path == "/portal/authorize"

    def _proxy_authenticated(self) -> bool:
        policy = self.app.security
        supplied = self.headers.get(INTERNAL_SECRET_HEADER, "")
        return bool(
            policy.proxy_secret
            and policy.trusted_proxy(self.client_address[0])
            and secrets.compare_digest(supplied, policy.proxy_secret)
        )

    def _enforce_proxy_boundary(self, path: str) -> bool:
        policy = self.app.security
        proxy_required = bool(policy.public_url) or self._is_portal_path(path)
        if not proxy_required:
            return True
        if not self._proxy_authenticated():
            self.send_error(HTTPStatus.NOT_FOUND)
            return False
        forwarded_scheme = self.headers.get("X-Forwarded-Proto", "").split(",", 1)[0].strip().lower()
        forwarded_host = normalize_host(self.headers.get("X-Forwarded-Host", "").split(",", 1)[0])
        if forwarded_scheme != "https" or not forwarded_host:
            self.send_error(HTTPStatus.MISDIRECTED_REQUEST, "HTTPS proxy metadata is required.")
            return False
        if policy.public_url and not self._is_portal_path(path) and forwarded_host != policy.public_host:
            self.send_error(HTTPStatus.MISDIRECTED_REQUEST, "Unexpected public host.")
            return False
        return True

    def _expected_origin(self, path: str) -> str:
        if self._is_portal_path(path):
            host = normalize_host(self.headers.get("X-Forwarded-Host", "").split(",", 1)[0])
            scheme = self.headers.get("X-Forwarded-Proto", "").split(",", 1)[0].strip().lower()
            return f"{scheme}://{host}" if scheme in {"http", "https"} and host else ""
        if self.app.security.public_url:
            return self.app.security.public_url
        host = normalize_host(self.headers.get("Host", ""))
        return f"http://{host}" if host else ""

    @staticmethod
    def _normalized_origin(value: str) -> str:
        try:
            parsed = urllib.parse.urlsplit(value)
        except ValueError:
            return ""
        host = normalize_host(parsed.netloc)
        if parsed.scheme not in {"http", "https"} or not host:
            return ""
        return f"{parsed.scheme.lower()}://{host}"

    def _origin_allowed(self, path: str) -> bool:
        expected = self._expected_origin(path)
        origin = self.headers.get("Origin", "")
        if origin:
            return secrets.compare_digest(self._normalized_origin(origin), expected)
        referer = self.headers.get("Referer", "")
        if referer:
            return secrets.compare_digest(self._normalized_origin(referer), expected)
        # Public and portal POST requests must provide browser same-origin metadata.
        return not (self.app.security.public_url or self._is_portal_path(path))

    def _client_ip(self) -> str:
        peer = self.client_address[0]
        if not self._proxy_authenticated():
            return peer
        forwarded = [item.strip() for item in self.headers.get("X-Forwarded-For", "").split(",") if item.strip()]
        for address in reversed(forwarded):
            if not self.app.security.trusted_proxy(address):
                return address
        return forwarded[0] if forwarded else peer

    def _login_keys(self, realm: str, username: str) -> tuple[str, str]:
        address = self._client_ip()
        return (
            f"{realm}:account:{username.strip().casefold()}",
            f"{realm}:address:{address}",
        )

    def _login_get(self, parsed: urllib.parse.SplitResult) -> None:
        query = urllib.parse.parse_qs(parsed.query)
        csrf = secrets.token_urlsafe(32)
        if parsed.path.startswith("/__caddy_ui_auth"):
            group_id = first(query, "group")
            group = self.app.access.get_group(group_id)
            if not group:
                self.send_error(HTTPStatus.NOT_FOUND, "Access group not found.")
                return
            return_to = safe_return_path(first(query, "return_to", "/"))
            content = secure_views.portal_login(group, csrf, first(query, "error"), return_to)
            self._html_with_cookie(content, self._cookie_header(LOGIN_CSRF_COOKIE, csrf, 600))
            return
        if self._secure_session(self._cookie(SESSION_COOKIE)):
            self._redirect("/")
            return
        content = secure_views.login(csrf, first(query, "error"))
        self._html_with_cookie(content, self._cookie_header(LOGIN_CSRF_COOKIE, csrf, 600))

    def _valid_login_csrf(self, form: dict[str, list[str]]) -> bool:
        cookie = self._cookie(LOGIN_CSRF_COOKIE)
        supplied = first(form, "login_csrf")
        return bool(cookie and supplied and secrets.compare_digest(cookie, supplied))

    def _login_post(self, form: dict[str, list[str]]) -> None:
        if not self._valid_login_csrf(form):
            self.send_error(HTTPStatus.FORBIDDEN, "Invalid login CSRF token.")
            return
        username = first(form, "username")[:80]
        password = first(form, "password")[:512]
        code = first(form, "totp")[:6]
        account_key, address_key = self._login_keys("admin", username)
        limits = (
            (account_key, self.app.security.account_attempts),
            (address_key, self.app.security.address_attempts),
        )
        if not self.app.throttle.allowed(limits):
            self._redirect("/login", error="Too many sign-in attempts. Try again later.", clear_login_csrf=True)
            return
        user = self.app.database.authenticate(username, password) if len(password) >= 14 else None
        valid_totp = bool(user and (not user["totp_enabled"] or verify_totp(user["totp_secret"], code)))
        if user and self.app.security.require_totp and not user["totp_enabled"]:
            self.app.audit.record(
                Actor(user["id"], user["username"], self._client_ip()),
                "login.totp_required",
                "session",
                "",
                result="failed",
            )
            self._redirect(
                "/login",
                error="Public access requires TOTP. Configure TOTP before enabling public access.",
                clear_login_csrf=True,
            )
            return
        if not user or not valid_totp:
            self.app.throttle.record_failure((account_key, address_key))
            self.app.audit.record(
                Actor(username=username or "unknown", remote_address=self._client_ip()),
                "login.failed",
                "session",
                "",
                result="failed",
            )
            self._redirect(
                "/login",
                error="Invalid username, password, or TOTP code.",
                clear_login_csrf=True,
            )
            return
        self.app.throttle.clear((account_key, address_key))
        ttl = self.app.settings.session_ttl_seconds
        if self.app.security.public_url:
            ttl = min(ttl, 28800)
        token, _ = self.app.database.create_session(
            user["id"], ttl, self._client_ip(), self.headers.get("User-Agent", "")
        )
        self.app.audit.record(
            Actor(user["id"], user["username"], self._client_ip()),
            "login.success",
            "session",
            token_hash(token)[:12],
        )
        self._redirect("/", set_session=token, clear_login_csrf=True, session_ttl=ttl)

    def _portal_authorize(self, parsed: urllib.parse.SplitResult) -> None:
        query = urllib.parse.parse_qs(parsed.query)
        group_id = first(query, "group")
        if not REFERENCE_RE.fullmatch(group_id):
            self.send_error(HTTPStatus.BAD_REQUEST, "Invalid access group.")
            return
        token = self._cookie(PORTAL_COOKIE_PREFIX + group_id)
        row = None
        if token:
            with self.app.database.connect() as connection:
                row = connection.execute(
                    """SELECT access_credentials.username, portal_sessions.remote_address,
                              portal_sessions.user_agent
                       FROM portal_sessions
                       JOIN access_credentials ON access_credentials.id=portal_sessions.credential_id
                       WHERE portal_sessions.token_hash=? AND portal_sessions.group_id=?
                         AND portal_sessions.expires_at>? AND access_credentials.enabled=1""",
                    (token_hash(token), group_id, utc_now()),
                ).fetchone()
        if row and self._session_context_valid(row["remote_address"], row["user_agent"]):
            self.send_response(HTTPStatus.OK)
            self.send_header("Remote-User", row["username"])
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", "0")
            self._security_headers()
            self.end_headers()
            return
        original = safe_return_path(self.headers.get("X-Forwarded-Uri", "/"))
        location = f"/__caddy_ui_auth/login?{urllib.parse.urlencode({'group': group_id, 'return_to': original})}"
        self.send_response(HTTPStatus.FOUND)
        self.send_header("Location", location)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", "0")
        self._security_headers()
        self.end_headers()

    def _portal_login_post(self, form: dict[str, list[str]]) -> None:
        if not self._valid_login_csrf(form):
            self.send_error(HTTPStatus.FORBIDDEN, "Invalid login CSRF token.")
            return
        group_id = first(form, "group")
        if not REFERENCE_RE.fullmatch(group_id):
            self.send_error(HTTPStatus.BAD_REQUEST)
            return
        group = self.app.access.get_group(group_id)
        if not group:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        username = first(form, "username")[:80]
        password = first(form, "password")[:512]
        account_key, address_key = self._login_keys(f"portal:{group_id}", username)
        limits = (
            (account_key, self.app.security.account_attempts),
            (address_key, self.app.security.address_attempts),
        )
        return_to = safe_return_path(first(form, "return_to", "/"))
        if not self.app.throttle.allowed(limits):
            self._portal_failure_redirect(group_id, return_to, "Too many sign-in attempts. Try again later.")
            return
        credential = self.app.access.authenticate(group_id, username, password) if len(password) >= 14 else None
        if not credential:
            self.app.throttle.record_failure((account_key, address_key))
            self.app.audit.record(
                Actor(username=username or "unknown", remote_address=self._client_ip()),
                "portal_login.failed",
                "access_group",
                group_id,
                result="failed",
            )
            self._portal_failure_redirect(group_id, return_to, "Invalid username or password.")
            return
        self.app.throttle.clear((account_key, address_key))
        token = secrets.token_urlsafe(32)
        now = datetime.now(UTC)
        expires = now + timedelta(seconds=self.app.security.portal_session_ttl_seconds)
        with self.app.database.transaction() as connection:
            connection.execute("DELETE FROM portal_sessions WHERE expires_at<?", (now.isoformat(),))
            connection.execute(
                """INSERT INTO portal_sessions(
                       token_hash,credential_id,group_id,expires_at,created_at,remote_address,user_agent
                   ) VALUES(?,?,?,?,?,?,?)""",
                (
                    token_hash(token),
                    credential["id"],
                    group_id,
                    expires.isoformat(),
                    now.isoformat(),
                    self._client_ip(),
                    self.headers.get("User-Agent", "")[:400],
                ),
            )
        self.app.audit.record(
            Actor(username=credential["username"], remote_address=self._client_ip()),
            "portal_login.success",
            "access_group",
            group_id,
        )
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", return_to)
        self.send_header(
            "Set-Cookie",
            self._cookie_header(
                PORTAL_COOKIE_PREFIX + group_id,
                token,
                self.app.security.portal_session_ttl_seconds,
            ),
        )
        self.send_header("Set-Cookie", self._cookie_header(LOGIN_CSRF_COOKIE, "", 0))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", "0")
        self._security_headers()
        self.end_headers()

    def _portal_failure_redirect(self, group_id: str, return_to: str, error: str) -> None:
        location = f"/__caddy_ui_auth/login?{urllib.parse.urlencode({'group': group_id, 'return_to': return_to, 'error': error})}"
        self._redirect(location, clear_login_csrf=True)

    def _session_context_valid(self, remote_address: str, user_agent: str) -> bool:
        current_agent = self.headers.get("User-Agent", "")[:400]
        if user_agent and not secrets.compare_digest(user_agent, current_agent):
            return False
        if self.app.security.bind_session_ip and remote_address:
            return secrets.compare_digest(remote_address, self._client_ip())
        return True

    def _secure_session(self, token: str) -> sqlite3.Row | None:
        if not token:
            return None
        with self.app.database.connect() as connection:
            row = connection.execute(
                """SELECT sessions.*, users.username, users.display_name, users.role, users.enabled, users.theme,
                          (SELECT value_json FROM settings WHERE key='accent') AS accent_json
                   FROM sessions JOIN users ON users.id=sessions.user_id
                   WHERE sessions.token_hash=? AND sessions.expires_at>? AND users.enabled=1""",
                (token_hash(token), utc_now()),
            ).fetchone()
        if not row or not self._session_context_valid(row["remote_address"], row["user_agent"]):
            return None
        return row

    def _require_session(self, api: bool) -> sqlite3.Row | None:
        session = self._secure_session(self._cookie(SESSION_COOKIE))
        if session:
            return session
        if api:
            self._json({"error": "authentication required"}, HTTPStatus.UNAUTHORIZED)
        else:
            self._redirect("/login", clear_session=True)
        return None

    def _cookie_header(self, name: str, value: str, max_age: int) -> str:
        secure = self.app.settings.secure_cookies or bool(self.app.security.public_url) or self._is_portal_path(self.path)
        secure_value = "; Secure" if secure else ""
        return (
            f"{name}={value}; Path=/; HttpOnly; SameSite=Strict; Max-Age={max_age}; Priority=High"
            f"{secure_value}"
        )

    def _html_with_cookie(self, content: bytes, cookie: str) -> None:
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Set-Cookie", cookie)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(content)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)

    def _html(self, content: bytes, status: HTTPStatus = HTTPStatus.OK) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(content)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)

    def _json(self, value: Any, status: HTTPStatus = HTTPStatus.OK) -> None:
        content = json.dumps(value, separators=(",", ":"), default=str).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(content)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)

    def _redirect(
        self,
        path: str,
        message: str = "",
        error: str = "",
        set_session: str = "",
        clear_session: bool = False,
        clear_login_csrf: bool = False,
        session_ttl: int | None = None,
    ) -> None:
        if message or error:
            separator = "&" if "?" in path else "?"
            path += separator + urllib.parse.urlencode({"message": message, "error": error})
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", path)
        if set_session:
            self.send_header(
                "Set-Cookie",
                self._cookie_header(
                    SESSION_COOKIE,
                    set_session,
                    session_ttl or self.app.settings.session_ttl_seconds,
                ),
            )
        if clear_session:
            self.send_header("Set-Cookie", self._cookie_header(SESSION_COOKIE, "", 0))
        if clear_login_csrf:
            self.send_header("Set-Cookie", self._cookie_header(LOGIN_CSRF_COOKIE, "", 0))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", "0")
        self._security_headers()
        self.end_headers()

    def _security_headers(self) -> None:
        super()._security_headers()
        if self.app.security.public_url or self._is_portal_path(self.path):
            self.send_header("Strict-Transport-Security", "max-age=31536000; includeSubDomains")
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Resource-Policy", "same-origin")
        self.send_header("X-Robots-Tag", "noindex, nofollow")

    def log_message(self, fmt: str, *args: Any) -> None:
        logging.info("%s - %s", self._client_ip(), fmt % args)


def create_secure_handler(application: SecureApplication) -> type[SecureHandler]:
    class BoundSecureHandler(SecureHandler):
        app = application

    return BoundSecureHandler


def main() -> int:
    logging.basicConfig(
        level=os.getenv("LOG_LEVEL", "INFO").upper(),
        format="%(asctime)s %(levelname)s %(message)s",
    )
    settings = Settings.from_environment()
    application = SecureApplication(settings)
    application.start_jobs()
    server = ThreadingHTTPServer((settings.host, settings.port), create_secure_handler(application))
    mode = f"public origin {application.security.public_url}" if application.security.public_url else "private origin"
    logging.info("Caddy UI v%s listening on %s:%s (%s)", __version__, settings.host, settings.port, mode)
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
