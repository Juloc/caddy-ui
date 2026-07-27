from __future__ import annotations

import hashlib
import hmac
import logging
import os
import secrets
import threading
import urllib.parse
from dataclasses import replace
from http import HTTPStatus

from . import __version__, views
from .audit import Actor
from .config import Settings
from .domain import ManagedRoute, RouteKind, Upstream
from .enhanced_web import Application as EnhancedApplication
from .hardened_caddy import HardenedSecurityCaddyManager
from .hardened_web import (
    AdminHandler as HardenedAdminHandler,
    BoundedThreadingHTTPServer,
    PortalHandler as HardenedPortalHandler,
    _safe_return_to,
    _validate_settings,
    create_handler,
)
from .web import SESSION_COOKIE, first


ADMIN_PROXY_HEADER = "X-Caddy-Admin-Secret"
PORTAL_PROXY_HEADER = "X-Caddy-Portal-Secret"
LOGIN_CSRF_COOKIE = "caddy_ui_login_csrf"
ADMIN_UPSTREAMS = {"caddy-ui:8098", "http://caddy-ui:8098"}
PORTAL_UPSTREAMS = {"caddy-ui:8099", "http://caddy-ui:8099"}
RESERVED_AUTH_HEADERS = {
    ADMIN_PROXY_HEADER.casefold(),
    PORTAL_PROXY_HEADER.casefold(),
    "x-caddy-portal-user",
    "remote-user",
}


def _public_host(origin: str) -> str:
    if not origin:
        return ""
    try:
        parsed = urllib.parse.urlsplit(origin)
        port = parsed.port
    except ValueError as exc:
        raise RuntimeError("CADDY_UI_PUBLIC_ORIGIN is malformed.") from exc
    if (
        parsed.scheme != "https"
        or not parsed.hostname
        or port not in {None, 443}
        or parsed.path not in {"", "/"}
        or parsed.query
        or parsed.fragment
        or parsed.username
        or parsed.password
    ):
        raise RuntimeError(
            "CADDY_UI_PUBLIC_ORIGIN must be a standard HTTPS origin without credentials, port, path, query, or fragment."
        )
    return parsed.hostname.lower().rstrip(".")


class PublicAuthCaddyManager(HardenedSecurityCaddyManager):
    def __init__(self, settings, database, audit):
        super().__init__(settings, database, audit)
        admin_secret = str(database.setting("admin_proxy_secret", "") or "")
        if len(admin_secret) < 43:
            admin_secret = secrets.token_urlsafe(48)
            database.set_setting("admin_proxy_secret", admin_secret)
        self.admin_secret = admin_secret
        self.public_host = _public_host(settings.public_origin)

    @staticmethod
    def _targets_admin(route: ManagedRoute) -> bool:
        return (
            route.enabled
            and route.kind == RouteKind.PROXY
            and not route.paths
            and not route.access_group_id
            and len(route.upstreams) == 1
            and route.upstreams[0].address.strip().lower() in ADMIN_UPSTREAMS
        )

    def _public_route(self, routes: list[ManagedRoute]) -> ManagedRoute | None:
        if not self.public_host:
            return None
        matches = [route for route in routes if route.effective_host.lower() == self.public_host]
        if len(matches) != 1 or not self._targets_admin(matches[0]):
            raise ValueError(
                f"Public Caddy UI host {self.public_host} must have exactly one enabled, unprotected catch-all proxy route to caddy-ui:8098."
            )
        return matches[0]

    def _validate_auth_transport(self, routes: list[ManagedRoute]) -> None:
        for route in routes:
            if any(header.name.casefold() in RESERVED_AUTH_HEADERS for header in route.request_headers):
                raise ValueError("Reserved authentication headers cannot be configured manually.")
            upstreams = {upstream.address.strip().lower() for upstream in route.upstreams}
            if upstreams & PORTAL_UPSTREAMS:
                raise ValueError("The internal access-portal listener cannot be used as a managed upstream.")
            admin_targets = upstreams & ADMIN_UPSTREAMS
            if admin_targets and upstreams - ADMIN_UPSTREAMS:
                raise ValueError("Caddy UI cannot be mixed with other upstreams in one route.")
            if admin_targets and not self.public_host:
                raise ValueError("CADDY_UI_PUBLIC_ORIGIN is required before Caddy UI can be exposed through a managed route.")

    def ensure_public_route(self) -> bool:
        if not self.public_host:
            return False
        routes = self.routes.list()
        matches = [route for route in routes if route.effective_host.lower() == self.public_host]
        if matches:
            self._validate_auth_transport(routes)
            self._public_route(routes)
            return False

        names = {route.name.casefold() for route in routes}
        name = "caddy-ui-admin"
        suffix = 2
        while name.casefold() in names:
            name = f"caddy-ui-admin-{suffix}"
            suffix += 1
        route = ManagedRoute(
            name=name,
            host=self.public_host,
            kind=RouteKind.PROXY,
            enabled=True,
            upstreams=[Upstream("caddy-ui:8098")],
        )
        self.apply(
            Actor(username="system", remote_address="local"),
            "Bootstrap hardened public Caddy UI route",
            proposed=route,
        )
        return True

    def _rendered_for(self, routes: list[ManagedRoute]) -> dict[str, str]:
        self._validate_auth_transport(routes)
        public_route = self._public_route(routes) if self.public_host else None
        content = super()._rendered_for(routes)
        public_filename = (
            f"site-{hashlib.sha256(self.public_host.encode('utf-8')).hexdigest()[:12]}.caddy"
            if public_route
            else ""
        )
        admin_injected = False

        for filename, value in list(content.items()):
            rendered: list[str] = []
            for line in value.splitlines():
                rendered.append(line)
                stripped = line.strip()
                if not (line.startswith("        reverse_proxy ") and stripped.endswith("{")):
                    continue
                target = stripped[len("reverse_proxy ") : -1].strip().lower()
                if filename == public_filename and target in ADMIN_UPSTREAMS:
                    rendered.extend(
                        [
                            f"            header_up {ADMIN_PROXY_HEADER} {self.admin_secret}",
                            f"            header_up -{PORTAL_PROXY_HEADER}",
                            "            header_up X-Forwarded-Host {host}",
                            "            header_up X-Forwarded-Proto {scheme}",
                        ]
                    )
                    admin_injected = True
                else:
                    rendered.append(f"            header_up -{ADMIN_PROXY_HEADER}")
                    if target not in PORTAL_UPSTREAMS:
                        rendered.append(f"            header_up -{PORTAL_PROXY_HEADER}")
            content[filename] = "\n".join(rendered) + "\n"

        if public_route and not admin_injected:
            raise ValueError("The hardened public Caddy UI route could not be rendered securely.")
        return content


class Application(EnhancedApplication):
    def __init__(self, settings: Settings):
        super().__init__(settings)
        self.caddy = PublicAuthCaddyManager(settings, self.database, self.audit)
        self.caddy.ensure_public_route()
        self.caddy.reconcile()


class LoginCsrfMixin:
    app: Application

    def _login_csrf_cookie_header(self, token: str, max_age: int = 600) -> str:
        secure = "; Secure" if self._secure_cookie() else ""
        return (
            f"{self._cookie_name(LOGIN_CSRF_COOKIE)}={token}; Path=/; HttpOnly; SameSite=Strict; "
            f"Max-Age={max_age}; Priority=High{secure}"
        )

    @staticmethod
    def _inject_login_csrf(content: bytes, token: str) -> bytes:
        marker = b'<form method="post"'
        start = content.find(marker)
        if start < 0:
            raise RuntimeError("Login form is missing.")
        end = content.find(b">", start)
        if end < 0:
            raise RuntimeError("Login form is malformed.")
        hidden = f'<input type="hidden" name="login_csrf" value="{token}">'.encode("utf-8")
        return content[: end + 1] + hidden + content[end + 1 :]

    def _login_html(self, content: bytes) -> None:
        token = secrets.token_urlsafe(32)
        content = self._inject_login_csrf(content, token)
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Set-Cookie", self._login_csrf_cookie_header(token))
        self._security_headers()
        self.end_headers()
        self.wfile.write(content)

    def _valid_login_csrf(self, form: dict[str, list[str]]) -> bool:
        cookie = self._cookie(LOGIN_CSRF_COOKIE)
        supplied = first(form, "login_csrf")
        return bool(cookie and supplied and hmac.compare_digest(cookie, supplied))


class AdminHandler(LoginCsrfMixin, HardenedAdminHandler):
    app: Application

    def _surface_allowed(self) -> bool:
        if not super()._surface_allowed():
            return False
        if not self.app.settings.public_origin:
            return True
        return self._peer_is_internal() and hmac.compare_digest(
            self.headers.get(ADMIN_PROXY_HEADER, ""),
            self.app.caddy.admin_secret,
        )

    def _login_get(self, parsed: urllib.parse.SplitResult) -> None:
        token = self._cookie(SESSION_COOKIE)
        session = self.app.database.session(token)
        user_agent = self.headers.get("User-Agent", "")[:400]
        if session and hmac.compare_digest(str(session["user_agent"]), user_agent):
            self._redirect("/")
            return
        if session:
            self.app.database.revoke_session(token)
        query = urllib.parse.parse_qs(parsed.query)
        self._login_html(views.login(first(query, "error")))

    def _login_post(self, form: dict[str, list[str]]) -> None:
        if not self._valid_login_csrf(form):
            self._empty(HTTPStatus.FORBIDDEN)
            return
        super()._login_post(form)


class PortalHandler(LoginCsrfMixin, HardenedPortalHandler):
    app: Application

    def _login_get(self, parsed: urllib.parse.SplitResult) -> None:
        query = urllib.parse.parse_qs(parsed.query)
        group = self.app.access.get_group(first(query, "group"))
        if not group:
            self._empty(HTTPStatus.NOT_FOUND)
            return
        return_to = _safe_return_to(first(query, "return_to", "/"))
        self._login_html(views.portal_login(group, first(query, "error"), return_to))

    def _portal_login_post(self, form: dict[str, list[str]]) -> None:
        if not self._valid_login_csrf(form):
            self._empty(HTTPStatus.FORBIDDEN)
            return
        super()._portal_login_post(form)


def main() -> int:
    logging.basicConfig(
        level=os.getenv("LOG_LEVEL", "INFO").upper(),
        format="%(asctime)s %(levelname)s %(message)s",
    )
    settings = Settings.from_environment()
    _validate_settings(settings)
    if settings.public_origin:
        _public_host(settings.public_origin)
        settings = replace(
            settings,
            secure_cookies=True,
            require_totp=True,
            session_ttl_seconds=min(settings.session_ttl_seconds, 28_800),
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
