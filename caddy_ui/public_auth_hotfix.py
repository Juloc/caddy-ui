from __future__ import annotations

import logging
import os
import threading
from dataclasses import replace

from . import __version__
from .audit import Actor
from .config import Settings
from .domain import RouteKind
from .enhanced_web import Application as EnhancedApplication
from .hardened_web import BoundedThreadingHTTPServer, _validate_settings, create_handler
from .public_auth import (
    ADMIN_UPSTREAMS,
    AdminHandler,
    PortalHandler,
    PublicAuthCaddyManager,
    _public_host,
)


class MigratingPublicAuthCaddyManager(PublicAuthCaddyManager):
    """Migrate the legacy Access-locked admin route to the built-in admin login."""

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
        compatible = (
            route.enabled
            and route.kind == RouteKind.PROXY
            and not route.paths
            and len(route.upstreams) == 1
            and route.upstreams[0].address.strip().lower() in ADMIN_UPSTREAMS
        )
        if not compatible:
            raise ValueError(
                f"Public Caddy UI host {self.public_host} must be an enabled catch-all proxy route to caddy-ui:8098."
            )

        if route.access_group_id:
            migrated = replace(route, access_group_id="")
            self.apply(
                Actor(username="system", remote_address="local"),
                "Migrate public Caddy UI route to built-in authentication",
                proposed=migrated,
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
