from __future__ import annotations

import hmac
import ipaddress
import logging
import os
import secrets
import threading
import time
import urllib.parse
from datetime import UTC, datetime, timedelta
from http import HTTPStatus
from http.server import ThreadingHTTPServer
from typing import Any

from . import __version__, views
from .audit import Actor
from .config import Settings
from .db import utc_now
from .enhanced_web import Application as EnhancedApplication
from .enhanced_web import Handler as EnhancedHandler
from .hardened_caddy import HardenedSecurityCaddyManager
from .security import DUMMY_PASSWORD_HASH, MAX_PASSWORD_LENGTH, token_hash, verify_password, verify_totp
from .web import Handler as BaseHandler
from .web import PORTAL_COOKIE_PREFIX, SESSION_COOKIE, first


PORTAL_PROXY_HEADER = "X-Caddy-Portal-Secret"
AUTH_PREFIX = "/__caddy_ui_auth/"


class BoundedThreadingHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 64

    def __init__(self, server_address, request_handler_class, max_workers: int = 64):
        self._worker_slots = threading.BoundedSemaphore(max_workers)
        super().__init__(server_address, request_handler_class)

    def process_request(self, request, client_address) -> None:
        if not self._worker_slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self._worker_slots.release()
            raise

    def process_request_thread(self, request, client_address) -> None:
        try:
            super().process_request_thread(request, client_address)
        finally:
            self._worker_slots.release()


class Application(EnhancedApplication):
    def __init__(self, settings: Settings):
        super().__init__(settings)
        self.caddy = HardenedSecurityCaddyManager(settings, self.database, self.audit)
        self.caddy.reconcile()


def _normalized_origin(value: str) -> str:
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc or parsed.username or parsed.password:
        return ""
    if parsed.path not in {"", "/"} or parsed.query or parsed.fragment:
        return ""
    return f"{parsed.scheme.lower()}://{parsed.netloc.lower()}"


def _safe_return_to(value: str) -> str:
    if not value or len(value) > 2048 or any(character in value for character in "\r\n"):
        return "/"
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme or parsed.netloc or not parsed.path.startswith("/") or parsed.path.startswith("//"):
        return "/"
    if parsed.path.startswith(AUTH_PREFIX):
        return "/"
    result = parsed.path
    if parsed.query:
        result += "?" + parsed.query
    return result


class SecurityHandler(EnhancedHandler):
    app: Application
    cookie_same_site = "Strict"

    def _peer_is_internal(self) -> bool:
        try:
            address = ipaddress.ip_address(self.client_address[0])
        except ValueError:
            return False
        return address.is_private or address.is_loopback

    def _forwarded(self, name: str) -> str:
        if not self._peer_is_internal():
            return ""
        return self.headers.get(name, "").split(",", 1)[0].strip()

    def _request_scheme(self) -> str:
        value = self._forwarded("X-Forwarded-Proto").lower()
        return value if value in {"http", "https"} else "http"

    def _request_host(self) -> str:
        value = self._forwarded("X-Forwarded-Host") or self.headers.get("Host", "")
        return value.strip().lower().rstrip(".")

    def _request_origin(self) -> str:
        host = self._request_host()
        return f"{self._request_scheme()}://{host}" if host else ""

    def _client_ip(self) -> str:
        peer = self.client_address[0]
        if not self._peer_is_internal():
            return peer
        forwarded = self.headers.get("X-Forwarded-For", "")
        for candidate in reversed([item.strip() for item in forwarded.split(",") if item.strip()]):
            try:
                return str(ipaddress.ip_address(candidate))
            except ValueError:
                continue
        return peer

    def _secure_cookie(self) -> bool:
        return bool(self.app.settings.secure_cookies or self.app.settings.public_origin or self._request_scheme() == "https")

    def _cookie_name(self, name: str) -> str:
        return f"__Host-{name}" if self._secure_cookie() else name

    def _cookie(self, name: str) -> str:
        return BaseHandler._cookie(self, self._cookie_name(name))

    def _cookie_header(self, name: str, value: str, max_age: int) -> str:
        secure = "; Secure" if self._secure_cookie() else ""
        return (
            f"{self._cookie_name(name)}={value}; Path=/; HttpOnly; SameSite={self.cookie_same_site}; "
            f"Max-Age={max_age}; Priority=High{secure}"
        )

    def _security_headers(self) -> None:
        BaseHandler._security_headers(self)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Pragma", "no-cache")
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Resource-Policy", "same-origin")
        self.send_header("X-Permitted-Cross-Domain-Policies", "none")
        if self._request_scheme() == "https":
            self.send_header("Strict-Transport-Security", "max-age=31536000")

    def _empty(self, status: HTTPStatus, **headers: str) -> None:
        self.send_response(status)
        for name, value in headers.items():
            self.send_header(name.replace("_", "-"), value)
        self.send_header("Content-Length", "0")
        self._security_headers()
        self.end_headers()

    def _same_origin(self, expected: str | None = None) -> bool:
        if self.headers.get("Sec-Fetch-Site", "").lower() == "cross-site":
            return False
        expected = _normalized_origin(expected or self._request_origin())
        if not expected:
            return False
        origin = self.headers.get("Origin", "")
        if origin:
            return hmac.compare_digest(_normalized_origin(origin), expected)
        referer = self.headers.get("Referer", "")
        if referer:
            parsed = urllib.parse.urlsplit(referer)
            return hmac.compare_digest(_normalized_origin(f"{parsed.scheme}://{parsed.netloc}"), expected)
        return False

    def _lookup_admin(self, username: str, password: str):
        with self.app.database.connect() as connection:
            row = connection.execute(
                "SELECT * FROM users WHERE username=? COLLATE NOCASE AND enabled=1",
                (username.strip(),),
            ).fetchone()
        candidate_hash = row["password_hash"] if row else DUMMY_PASSWORD_HASH
        valid = verify_password(password if len(password) <= MAX_PASSWORD_LENGTH else "", candidate_hash)
        return row if row and valid else None

    def _lookup_portal_credential(self, group_id: str, username: str, password: str):
        with self.app.database.connect() as connection:
            row = connection.execute(
                "SELECT * FROM access_credentials WHERE group_id=? AND username=? COLLATE NOCASE AND enabled=1",
                (group_id, username.strip()),
            ).fetchone()
        candidate_hash = row["password_hash"] if row else DUMMY_PASSWORD_HASH
        valid = verify_password(password if len(password) <= MAX_PASSWORD_LENGTH else "", candidate_hash)
        return row if row and valid else None

    def _bound_portal_token_hash(self, token: str) -> str:
        return token_hash(token + "\0" + self.headers.get("User-Agent", "")[:400])


class AdminHandler(SecurityHandler):
    cookie_same_site = "Strict"

    def _public_origin(self) -> str:
        return _normalized_origin(self.app.settings.public_origin)

    def _surface_allowed(self) -> bool:
        expected = self._public_origin()
        if not expected:
            return True
        return self._request_scheme() == "https" and hmac.compare_digest(self._request_origin(), expected)

    def do_GET(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        if parsed.path == "/portal/authorize" or parsed.path.startswith(AUTH_PREFIX):
            self._empty(HTTPStatus.NOT_FOUND)
            return
        if parsed.path != "/api/health" and not self._surface_allowed():
            self._empty(HTTPStatus.MISDIRECTED_REQUEST)
            return
        super().do_GET()

    def do_POST(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        if parsed.path == "/portal/authorize" or parsed.path.startswith(AUTH_PREFIX):
            self._empty(HTTPStatus.NOT_FOUND)
            return
        if not self._surface_allowed():
            self._empty(HTTPStatus.MISDIRECTED_REQUEST)
            return
        if parsed.path == "/login" and not self._same_origin(self._public_origin() or self._request_origin()):
            self._empty(HTTPStatus.FORBIDDEN)
            return
        super().do_POST()

    def _login_get(self, parsed: urllib.parse.SplitResult) -> None:
        session = self.app.database.session(self._cookie(SESSION_COOKIE))
        user_agent = self.headers.get("User-Agent", "")[:400]
        if session and hmac.compare_digest(str(session["user_agent"]), user_agent):
            self._redirect("/")
            return
        self._html(views.login(first(urllib.parse.parse_qs(parsed.query), "error")))

    def _login_post(self, form: dict[str, list[str]]) -> None:
        username = first(form, "username").strip()
        password = first(form, "password")
        code = first(form, "totp")
        client_ip = self._client_ip()
        state = self.app.security.login_state("ui", client_ip, username)
        if not state["allowed"]:
            self._empty(HTTPStatus.TOO_MANY_REQUESTS, Retry_After=str(state["retry_after"]))
            return
        if state["delay"]:
            time.sleep(min(float(state["delay"]), 2.0))

        user = self._lookup_admin(username, password)
        valid_totp = bool(user) and (
            (not user["totp_enabled"] and not self.app.settings.require_totp)
            or (user["totp_enabled"] and verify_totp(user["totp_secret"], code))
        )
        if not user or not valid_totp:
            self.app.security.record_login_failure("ui", client_ip, username, self._request_host())
            self.app.audit.record(
                Actor(username=username or "unknown", remote_address=client_ip),
                "login.failed",
                "session",
                "",
                result="failed",
            )
            self._redirect("/login", error="Invalid username, password, or TOTP code.")
            return

        self.app.security.clear_login("ui", client_ip, username)
        token, _ = self.app.database.create_session(
            user["id"],
            self.app.settings.session_ttl_seconds,
            client_ip,
            self.headers.get("User-Agent", ""),
        )
        self.app.audit.record(
            Actor(user["id"], user["username"], client_ip),
            "login.success",
            "session",
            token_hash(token)[:12],
        )
        self._redirect("/", set_session=token)

    def _require_session(self, api: bool):
        token = self._cookie(SESSION_COOKIE)
        session = self.app.database.session(token)
        user_agent = self.headers.get("User-Agent", "")[:400]
        if session and hmac.compare_digest(str(session["user_agent"]), user_agent):
            return session
        if session:
            self.app.database.revoke_session(token)
            self.app.audit.record(
                Actor(session["user_id"], session["username"], self._client_ip()),
                "session.binding_mismatch",
                "session",
                token_hash(token)[:12],
                result="failed",
            )
        if api:
            self._json({"error": "authentication required"}, HTTPStatus.UNAUTHORIZED)
        else:
            self._redirect("/login", clear_session=True)
        return None


class PortalHandler(SecurityHandler):
    cookie_same_site = "Lax"

    def _proxy_allowed(self) -> bool:
        return (
            self._peer_is_internal()
            and hmac.compare_digest(
                self.headers.get(PORTAL_PROXY_HEADER, ""),
                self.app.caddy.portal_secret,
            )
            and bool(self._request_host())
        )

    def do_GET(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        if not self._proxy_allowed():
            self._empty(HTTPStatus.NOT_FOUND)
            return
        if parsed.path not in {
            "/portal/authorize",
            "/__caddy_ui_auth/login",
            "/__caddy_ui_auth/static/app.css",
            "/__caddy_ui_auth/static/app.js",
        }:
            self._empty(HTTPStatus.NOT_FOUND)
            return
        BaseHandler.do_GET(self)

    def do_POST(self) -> None:
        parsed = urllib.parse.urlsplit(self.path)
        if not self._proxy_allowed() or parsed.path != "/__caddy_ui_auth/login":
            self._empty(HTTPStatus.NOT_FOUND)
            return
        if not self._same_origin(self._request_origin()):
            self._empty(HTTPStatus.FORBIDDEN)
            return
        BaseHandler.do_POST(self)

    def _portal_authorize(self, parsed: urllib.parse.SplitResult) -> None:
        group_id = first(urllib.parse.parse_qs(parsed.query), "group")
        group = self.app.access.get_group(group_id)
        if not group:
            self._empty(HTTPStatus.FORBIDDEN)
            return

        raw_original = self.headers.get("X-Forwarded-Uri", "/")
        if urllib.parse.urlsplit(raw_original).path.startswith(AUTH_PREFIX):
            self._empty(HTTPStatus.UNAUTHORIZED)
            return
        original = _safe_return_to(raw_original)

        token = self._cookie(PORTAL_COOKIE_PREFIX + group_id)
        with self.app.database.connect() as connection:
            row = (
                connection.execute(
                    """SELECT access_credentials.username FROM portal_sessions
                       JOIN access_credentials ON access_credentials.id=portal_sessions.credential_id
                       WHERE portal_sessions.token_hash=? AND portal_sessions.group_id=?
                         AND portal_sessions.expires_at>? AND access_credentials.enabled=1""",
                    (self._bound_portal_token_hash(token), group_id, utc_now()),
                ).fetchone()
                if token
                else None
            )
        if row:
            self.send_response(HTTPStatus.OK)
            self.send_header("Remote-User", row["username"])
            self.send_header("Content-Length", "0")
            self._security_headers()
            self.end_headers()
            return

        location = f"/__caddy_ui_auth/login?{urllib.parse.urlencode({'group': group_id, 'return_to': original})}"
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", location)
        self.send_header("Content-Length", "0")
        self._security_headers()
        self.end_headers()

    def _portal_login_post(self, form: dict[str, list[str]]) -> None:
        group_id = first(form, "group")
        group = self.app.access.get_group(group_id)
        if not group:
            self._empty(HTTPStatus.NOT_FOUND)
            return

        username = first(form, "username").strip()
        password = first(form, "password")
        return_to = _safe_return_to(first(form, "return_to", "/"))
        client_ip = self._client_ip()
        state = self.app.security.login_state("portal:" + group_id, client_ip, username)
        if not state["allowed"]:
            self._empty(HTTPStatus.TOO_MANY_REQUESTS, Retry_After=str(state["retry_after"]))
            return
        if state["delay"]:
            time.sleep(min(float(state["delay"]), 2.0))

        credential = self._lookup_portal_credential(group_id, username, password)
        if not credential:
            self.app.security.record_login_failure(
                "portal:" + group_id,
                client_ip,
                username,
                self._request_host(),
            )
            self.app.audit.record(
                Actor(username=username or "unknown", remote_address=client_ip),
                "portal_login.failed",
                "access_group",
                group_id,
                result="failed",
            )
            location = f"/__caddy_ui_auth/login?{urllib.parse.urlencode({'group': group_id, 'return_to': return_to, 'error': 'Invalid username or password.'})}"
            self._redirect(location)
            return

        self.app.security.clear_login("portal:" + group_id, client_ip, username)
        token = secrets.token_urlsafe(32)
        now = datetime.now(UTC)
        ttl = self.app.settings.portal_session_ttl_seconds
        with self.app.database.transaction() as connection:
            connection.execute("DELETE FROM portal_sessions WHERE expires_at<?", (now.isoformat(),))
            connection.execute(
                "INSERT INTO portal_sessions(token_hash,credential_id,group_id,expires_at) VALUES(?,?,?,?)",
                (
                    self._bound_portal_token_hash(token),
                    credential["id"],
                    group_id,
                    (now + timedelta(seconds=ttl)).isoformat(),
                ),
            )
        self.app.audit.record(
            Actor(username=credential["username"], remote_address=client_ip),
            "portal_login.success",
            "access_group",
            group_id,
        )
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", return_to)
        self.send_header("Set-Cookie", self._cookie_header(PORTAL_COOKIE_PREFIX + group_id, token, ttl))
        self.send_header("Content-Length", "0")
        self._security_headers()
        self.end_headers()


def create_handler(application: Application, handler_type: type[SecurityHandler]) -> type[SecurityHandler]:
    class BoundHandler(handler_type):
        app = application

    return BoundHandler


def _validate_settings(settings: Settings) -> None:
    if settings.port == settings.portal_port:
        raise RuntimeError("UI_PORT and UI_PORTAL_PORT must use different ports.")
    if settings.public_origin:
        origin = _normalized_origin(settings.public_origin)
        if not origin or not origin.startswith("https://"):
            raise RuntimeError("CADDY_UI_PUBLIC_ORIGIN must be an HTTPS origin without a path.")


def main() -> int:
    logging.basicConfig(
        level=os.getenv("LOG_LEVEL", "INFO").upper(),
        format="%(asctime)s %(levelname)s %(message)s",
    )
    settings = Settings.from_environment()
    _validate_settings(settings)
    application = Application(settings)
    application.start_jobs()

    admin_server = BoundedThreadingHTTPServer(
        (settings.host, settings.port),
        create_handler(application, AdminHandler),
    )
    portal_server = BoundedThreadingHTTPServer(
        (settings.host, settings.portal_port),
        create_handler(application, PortalHandler),
    )
    portal_thread = threading.Thread(
        target=portal_server.serve_forever,
        name="caddy-ui-portal",
        daemon=True,
    )
    portal_thread.start()
    logging.info(
        "Caddy UI v%s listening on admin=%s:%s portal=%s:%s",
        __version__,
        settings.host,
        settings.port,
        settings.host,
        settings.portal_port,
    )
    try:
        admin_server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        admin_server.server_close()
        portal_server.shutdown()
        portal_server.server_close()
        application.stop_jobs()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
