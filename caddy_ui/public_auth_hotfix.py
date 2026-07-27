from __future__ import annotations

import hmac
import ipaddress
import logging
import os
import re
import secrets
import threading
import urllib.parse
from dataclasses import replace
from http import HTTPStatus

from . import __version__
from .audit import Actor
from .config import Settings
from .domain import ManagedRoute, RouteKind, Upstream
from .enhanced_web import Application as EnhancedApplication
from .hardened_web import BoundedThreadingHTTPServer, _validate_settings, create_handler
from .public_auth import (
    ADMIN_PROXY_HEADER,
    LOGIN_CSRF_COOKIE,
    AdminHandler as PublicAdminHandler,
    PortalHandler,
    PublicAuthCaddyManager,
    _public_host,
)


LOGIN_CSRF_TOKEN_RE = re.compile(r"^[A-Za-z0-9_-]{43}$")


def stable_login_csrf_token(existing: str) -> str:
    """Reuse a valid short-lived token so parallel login GETs cannot invalidate a form."""
    if LOGIN_CSRF_TOKEN_RE.fullmatch(existing):
        return existing
    return secrets.token_urlsafe(32)


def local_admin_host(value: str) -> bool:
    """Return whether a raw Host header targets localhost or a private/link-local IP."""
    try:
        hostname = urllib.parse.urlsplit(f"//{value.strip()}").hostname
    except ValueError:
        return False
    if not hostname:
        return False
    normalized = hostname.casefold().rstrip(".")
    if normalized == "localhost" or normalized.endswith(".localhost"):
        return True
    try:
        address = ipaddress.ip_address(normalized)
    except ValueError:
        return False
    return address.is_private or address.is_loopback or address.is_link_local


class AdminHandler(PublicAdminHandler):
    def _via_public_proxy(self) -> bool:
        return self._peer_is_internal() and hmac.compare_digest(
            self.headers.get(ADMIN_PROXY_HEADER, ""),
            self.app.caddy.admin_secret,
        )

    def _public_origin(self) -> str:
        # Only trust the configured public origin when Caddy authenticated the hop.
        if self._via_public_proxy():
            return super()._public_origin()
        return ""

    def _surface_allowed(self) -> bool:
        if self._via_public_proxy():
            return super()._surface_allowed()
        return self._peer_is_internal() and local_admin_host(self.headers.get("Host", ""))

    def _same_origin(self, expected: str | None = None) -> bool:
        if super()._same_origin(expected):
            return True
        # Chromium/Brave can emit Origin: null for a same-page form submission.
        # The double-submit login CSRF token is still validated in _login_post.
        return (
            self.headers.get("Origin", "").strip().casefold() == "null"
            and self.headers.get("Sec-Fetch-Site", "").strip().casefold() != "cross-site"
            and self._surface_allowed()
        )

    def _secure_cookie(self) -> bool:
        # Public access is HTTPS through the authenticated Caddy hop. Direct LAN
        # access is intentionally HTTP and receives separate non-__Host cookies.
        return self._via_public_proxy()

    def do_GET(self) -> None:
        if urllib.parse.urlsplit(self.path).path == "/favicon.ico":
            self._empty(HTTPStatus.NO_CONTENT)
            return
        super().do_GET()

    def _login_html(self, content: bytes) -> None:
        token = stable_login_csrf_token(self._cookie(LOGIN_CSRF_COOKIE))
        content = self._inject_login_csrf(content, token)
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Set-Cookie", self._login_csrf_cookie_header(token))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)


class MigratingPublicAuthCaddyManager(PublicAuthCaddyManager):
    """Reserve the configured public origin for the built-in admin login."""

    def _canonical_public_route(self, route: ManagedRoute) -> ManagedRoute:
        return replace(
            route,
            host=self.public_host,
            domain="",
            kind=RouteKind.PROXY,
            enabled=True,
            paths=[],
            upstreams=[Upstream("caddy-ui:8098")],
            request_headers=[],
            response_headers=[],
            load_balancing="random",
            health_uri="",
            health_interval="30s",
            tls_skip_verify=False,
            redirect_to="",
            redirect_status=308,
            access_group_id="",
            custom_snippet="",
        )

    def ensure_public_route(self) -> bool:
        if not self.public_host:
            return False

        routes = self.routes.list()
        matches = [route for route in routes if route.effective_host.lower() == self.public_host]
        if not matches:
            return super().ensure_public_route()
        if len(matches) != 1:
            raise ValueError(
                f"Public Caddy UI host {self.public_host} must have exactly one managed route."
            )

        route = matches[0]
        canonical = self._canonical_public_route(route)
        if route != canonical:
            self.apply(
                Actor(username="system", remote_address="local"),
                "Normalize public Caddy UI route for built-in authentication",
                proposed=canonical,
            )
            logging.warning(
                "Normalized legacy public Caddy UI route %s to the secured caddy-ui:8098 catch-all route.",
                route.name,
            )
            return True

        self._validate_auth_transport(routes)
        self._public_route(routes)
        return False


class Application(EnhancedApplication):
    def __init__(self, settings: Settings):
        super().__init__(settings)
        self.caddy = MigratingPublicAuthCaddyManager(settings, self.database, self.audit)
        self.caddy.ensure_public_route()
        self.caddy.reconcile()


def public_settings(settings: Settings) -> Settings:
    if not settings.public_origin:
        return replace(settings, require_totp=False)
    _public_host(settings.public_origin)
    return replace(
        settings,
        secure_cookies=True,
        require_totp=False,
        session_ttl_seconds=min(settings.session_ttl_seconds, 28_800),
    )


def main() -> int:
    logging.basicConfig(
        level=os.getenv("LOG_LEVEL", "INFO").upper(),
        format="%(asctime)s %(levelname)s %(message)s",
    )
    settings = public_settings(Settings.from_environment())
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
