from __future__ import annotations

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


class AdminHandler(PublicAdminHandler):
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
        return settings
    _public_host(settings.public_origin)
    return replace(
        settings,
        secure_cookies=True,
        session_ttl_seconds=min(settings.session_ttl_seconds, 28_800),
    )


def main() -> int:
    logging.basicConfig(
        level=os.getenv("LOG_LEVEL", "INFO").upper(),
        format="%(asctime)s %(levelname)s %(message)s",
    )
    settings = public_settings(Settings.from_environment())
    _validate_settings(settings)
    if settings.public_origin and not settings.require_totp:
        logging.warning(
            "Public Caddy UI access is enabled without mandatory TOTP. Enable TOTP for the account, then set CADDY_UI_REQUIRE_TOTP=true."
        )

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
