from __future__ import annotations

import logging
import os
import uuid
from http.server import ThreadingHTTPServer

from . import __version__
from .audit import Actor
from .config import Settings
from .domain import ManagedRoute, RouteKind, Upstream
from .secure_caddy import route_targets_caddy_ui
from .secure_web import SecureApplication, create_secure_handler


class PublicApplication(SecureApplication):
    def __init__(self, settings: Settings):
        super().__init__(settings)
        self._ensure_public_route()

    def _ensure_public_route(self) -> None:
        if not self.security.public_url:
            return
        routes = self.routes.list()
        matching = [route for route in routes if route.effective_host == self.security.public_host]
        valid = [
            route
            for route in matching
            if route.enabled
            and route.kind == RouteKind.PROXY
            and not route.paths
            and route_targets_caddy_ui(route)
        ]
        if len(valid) == 1 and len(matching) == 1:
            return
        if matching:
            raise RuntimeError(
                f"CADDY_UI_PUBLIC_URL host {self.security.public_host} is already used by an incompatible route."
            )
        names = {route.name.casefold() for route in routes}
        base_name = "caddy-ui-admin"
        name = base_name
        counter = 2
        while name.casefold() in names:
            name = f"{base_name}-{counter}"
            counter += 1
        route = ManagedRoute(
            id=str(uuid.uuid4()),
            name=name,
            host=self.security.public_host,
            kind=RouteKind.PROXY,
            upstreams=[Upstream("caddy-ui:8098")],
        )
        self.caddy.apply(
            Actor(username="system"),
            "Bootstrap hardened public Caddy UI route",
            proposed=route,
        )


def main() -> int:
    logging.basicConfig(
        level=os.getenv("LOG_LEVEL", "INFO").upper(),
        format="%(asctime)s %(levelname)s %(message)s",
    )
    settings = Settings.from_environment()
    application = PublicApplication(settings)
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
