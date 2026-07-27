from __future__ import annotations

import hashlib
import json
import shutil
import tempfile
from pathlib import Path

from . import caddy as base
from .audit import Actor
from .domain import ManagedRoute, RouteKind


PORTAL_BACKEND = "caddy-ui:8099"
PORTAL_PREFIX = "/__caddy_ui_auth/"


def render_route(route: ManagedRoute) -> str:
    route.validate()
    metadata = json.dumps(
        {"id": route.id, "name": route.name, "kind": route.kind.value, "host": route.host},
        separators=(",", ":"),
        sort_keys=True,
    )
    if not route.enabled:
        return f"{base.MANAGED_HEADER}\n{base.META_PREFIX} {metadata}\n# disabled\n"

    matcher = f"caddy_ui_{base._safe_identifier(route.id)}"
    matchers = [f"host {route.effective_host}"]
    if route.paths:
        matchers.append("path " + " ".join(base._quote(path) for path in route.paths))
    lines = [f"    {base.META_PREFIX} {metadata}", f"    @{matcher} {{"]
    lines.extend(f"        {item}" for item in matchers)
    lines.extend(["    }", f"    handle @{matcher} {{"])

    if route.access_group_id:
        lines.extend(
            [
                f"        forward_auth {PORTAL_BACKEND} {{",
                f"            uri /portal/authorize?group={route.access_group_id}",
                "            header_up X-Caddy-Portal-Proxy 1",
                "            copy_headers Remote-User>X-Caddy-Portal-User",
                "        }",
            ]
        )

    if route.kind == RouteKind.REDIRECT:
        lines.append(f"        redir {base._quote(route.redirect_to)} {route.redirect_status}")
    elif route.kind == RouteKind.CUSTOM:
        for custom_line in route.custom_snippet.strip().splitlines():
            lines.append(f"        {custom_line.rstrip()}")
    else:
        lines.extend(base._render_headers("header", route.response_headers, "        "))
        addresses = " ".join(base._quote(upstream.address) for upstream in route.upstreams)
        lines.append(f"        reverse_proxy {addresses} {{")
        if len(route.upstreams) > 1:
            lines.append("            lb_policy " + route.load_balancing)
        if route.health_uri:
            lines.extend(
                [
                    f"            health_uri {base._quote(route.health_uri)}",
                    f"            health_interval {route.health_interval}",
                ]
            )
        lines.extend(base._render_headers("header_up", route.request_headers, "            "))
        if route.access_group_id:
            lines.append("            header_up Remote-User {http.request.header.X-Caddy-Portal-User}")
        else:
            lines.append("            header_up -Remote-User")
        lines.append("            header_up -X-Caddy-Portal-User")
        if route.tls_skip_verify:
            lines.extend(["            transport http {", "                tls_insecure_skip_verify", "            }"])
        lines.append("        }")
    lines.extend(["    }", ""])
    return "\n".join(lines)


def render_site(host: str, routes: list[ManagedRoute], tls_lines: list[str] | None = None) -> str:
    lines = [base.MANAGED_HEADER, f"{host} {{", "    encode zstd gzip"]
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

    protected = any(route.enabled and route.access_group_id for route in routes)
    if protected:
        lines.extend(
            [
                "    @caddy_ui_portal path /__caddy_ui_auth/*",
                "    handle @caddy_ui_portal {",
                f"        reverse_proxy {PORTAL_BACKEND} {{",
                "            header_up X-Caddy-Portal-Proxy 1",
                "            header_up X-Forwarded-Host {host}",
                "            header_up X-Forwarded-Proto {scheme}",
                "            header_up X-Forwarded-Uri {uri}",
                "        }",
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


class SecureCaddyManager(base.CaddyManager):
    def _rendered_for(self, routes: list[ManagedRoute]) -> dict[str, str]:
        grouped: dict[str, list[ManagedRoute]] = {}
        for route in routes:
            grouped.setdefault(route.effective_host, []).append(route)

        content: dict[str, str] = {}
        providers = self.providers.list()
        for host, host_routes in sorted(grouped.items()):
            enabled = [route for route in host_routes if route.enabled]
            catch_all = [route for route in enabled if not route.paths]
            if len(catch_all) > 1:
                names = ", ".join(sorted(route.name for route in catch_all))
                raise ValueError(f"Host {host} has multiple catch-all routes: {names}.")

            claimed_paths: dict[str, str] = {}
            for route in enabled:
                for path in route.paths:
                    if path.startswith(PORTAL_PREFIX):
                        raise ValueError(f"Path {path} on {host} uses the reserved access portal prefix.")
                    if path in claimed_paths:
                        raise ValueError(f"Path {path} on {host} is used by both {claimed_paths[path]} and {route.name}.")
                    claimed_paths[path] = route.name

            ordered_routes = sorted(
                host_routes,
                key=lambda route: (
                    not bool(route.paths),
                    -max((len(path) for path in route.paths), default=0),
                    route.name.lower(),
                ),
            )
            tls_lines: list[str] = []
            domain = ordered_routes[0].domain
            provider = next(
                (item for item in providers if domain in item.get("domains", []) and item.get("type") == "netcup"),
                None,
            )
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
            content[f"site-{digest}.caddy"] = render_site(host, ordered_routes, tls_lines)
        return content

    def _restore_revision(self, actor: Actor, revision_id: str) -> None:
        with self.database.connect() as connection:
            row = connection.execute(
                "SELECT content_json FROM revisions WHERE id=? AND applied=1",
                (revision_id,),
            ).fetchone()
        if not row:
            raise ValueError("Revision not found.")

        snapshot = json.loads(row[0])
        route_values = snapshot.get("routes")
        if not isinstance(route_values, list):
            raise ValueError("Legacy revisions without route metadata cannot be restored securely.")
        routes = [ManagedRoute.from_json(item) for item in route_values]

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
                content = self.rendered()
                self._write_managed_files(content)
                self.validate()
                if self.settings.auto_reload:
                    self.reload()
                new_revision = self._create_revision(actor, f"Restore {revision_id}", content)
                self._mark_revision_applied(new_revision)
                self.audit.record(
                    actor,
                    "revision.restore",
                    "revision",
                    revision_id,
                    after={"new_revision": new_revision},
                )
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
