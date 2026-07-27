from __future__ import annotations

import hashlib
import json

from .caddy import CaddyManager, MANAGED_HEADER, META_PREFIX, _quote, _render_headers, _safe_identifier
from .domain import ManagedRoute, RouteKind
from .security_policy import INTERNAL_SECRET_HEADER, SecurityPolicy


PROXY_SECRET_ENV = "{env.CADDY_UI_PROXY_SECRET}"


def is_caddy_ui_upstream(address: str) -> bool:
    value = address.strip().lower().rstrip("/")
    return value in {"caddy-ui:8098", "http://caddy-ui:8098", "https://caddy-ui:8098"}


def route_targets_caddy_ui(route: ManagedRoute) -> bool:
    return bool(route.upstreams) and all(is_caddy_ui_upstream(item.address) for item in route.upstreams)


def _validate_auth_transport(route: ManagedRoute) -> None:
    ui_targets = [is_caddy_ui_upstream(item.address) for item in route.upstreams]
    if any(ui_targets) and not all(ui_targets):
        raise ValueError("Caddy UI cannot be mixed with other upstreams in one route.")
    reserved = {INTERNAL_SECRET_HEADER.casefold()}
    if route.access_group_id:
        reserved.add("remote-user")
    if any(header.name.casefold() in reserved for header in route.request_headers):
        raise ValueError("Reserved authentication headers cannot be configured manually.")


def render_hardened_route(route: ManagedRoute) -> str:
    route.validate()
    _validate_auth_transport(route)
    metadata = json.dumps(
        {"id": route.id, "name": route.name, "kind": route.kind.value, "host": route.host},
        separators=(",", ":"),
        sort_keys=True,
    )
    if not route.enabled:
        return f"{MANAGED_HEADER}\n{META_PREFIX} {metadata}\n# disabled\n"

    matcher = f"caddy_ui_{_safe_identifier(route.id)}"
    matchers = [f"host {route.effective_host}"]
    if route.paths:
        matchers.append("path " + " ".join(_quote(path) for path in route.paths))
    lines = [f"    {META_PREFIX} {metadata}", f"    @{matcher} {{"]
    lines.extend(f"        {item}" for item in matchers)
    lines.extend(["    }", f"    handle @{matcher} {{", "        route {"])

    lines.extend(
        [
            f"            request_header -{INTERNAL_SECRET_HEADER}",
            "            request_header -Remote-User",
        ]
    )
    if route.access_group_id:
        lines.extend(
            [
                "            forward_auth caddy-ui:8098 {",
                f"                uri /portal/authorize?group={route.access_group_id}",
                f"                header_up {INTERNAL_SECRET_HEADER} {PROXY_SECRET_ENV}",
                "                copy_headers Remote-User",
                "            }",
            ]
        )

    if route.kind == RouteKind.REDIRECT:
        lines.append(f"            redir {_quote(route.redirect_to)} {route.redirect_status}")
    elif route.kind == RouteKind.CUSTOM:
        for custom_line in route.custom_snippet.strip().splitlines():
            lines.append(f"            {custom_line.rstrip()}")
    else:
        lines.extend(_render_headers("header", route.response_headers, "            "))
        addresses = " ".join(_quote(upstream.address) for upstream in route.upstreams)
        lines.append(f"            reverse_proxy {addresses} {{")
        if len(route.upstreams) > 1:
            lines.append("                lb_policy " + route.load_balancing)
        if route.health_uri:
            lines.extend(
                [
                    f"                health_uri {_quote(route.health_uri)}",
                    f"                health_interval {route.health_interval}",
                ]
            )
        lines.extend(_render_headers("header_up", route.request_headers, "                "))
        if route_targets_caddy_ui(route):
            lines.append(f"                header_up {INTERNAL_SECRET_HEADER} {PROXY_SECRET_ENV}")
        else:
            lines.append(f"                header_up -{INTERNAL_SECRET_HEADER}")
        if route.tls_skip_verify:
            lines.extend(
                [
                    "                transport http {",
                    "                    tls_insecure_skip_verify",
                    "                }",
                ]
            )
        lines.append("            }")
    lines.extend(["        }", "    }", ""])
    return "\n".join(lines)


def render_hardened_site(host: str, routes: list[ManagedRoute], tls_lines: list[str] | None = None) -> str:
    lines = [MANAGED_HEADER, f"{host} {{", "    encode zstd gzip"]
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
    if any(route.enabled and route.access_group_id for route in routes):
        lines.extend(
            [
                "    handle /__caddy_ui_auth/* {",
                "        reverse_proxy caddy-ui:8098 {",
                f"            header_up {INTERNAL_SECRET_HEADER} {PROXY_SECRET_ENV}",
                "        }",
                "    }",
            ]
        )
    for route in routes:
        if route.enabled:
            lines.extend(render_hardened_route(route).splitlines())
        else:
            lines.append(f"    # disabled route: {route.name}")
    lines.extend(["    handle {", '        respond "Service not configured" 404', "    }", "}", ""])
    return "\n".join(lines)


class HardenedCaddyManager(CaddyManager):
    def __init__(self, settings, database, audit, security_policy: SecurityPolicy):
        super().__init__(settings, database, audit)
        self.security_policy = security_policy

    def _rendered_for(self, routes: list[ManagedRoute]) -> dict[str, str]:
        grouped: dict[str, list[ManagedRoute]] = {}
        for route in routes:
            grouped.setdefault(route.effective_host, []).append(route)
        content: dict[str, str] = {}
        providers = self.providers.list()
        for host, host_routes in sorted(grouped.items()):
            enabled = [route for route in host_routes if route.enabled]
            protected = any(route.access_group_id or route_targets_caddy_ui(route) for route in enabled)
            if protected and not self.security_policy.proxy_secret:
                raise ValueError(
                    "CADDY_UI_PROXY_SECRET is required for access portals and public Caddy UI routes."
                )
            ui_routes = [route for route in enabled if route_targets_caddy_ui(route)]
            if ui_routes:
                if not self.security_policy.public_url:
                    raise ValueError(
                        "CADDY_UI_PUBLIC_URL is required before Caddy UI can be exposed through a managed route."
                    )
                if host != self.security_policy.public_host:
                    raise ValueError(
                        f"The Caddy UI route host must match CADDY_UI_PUBLIC_URL ({self.security_policy.public_host})."
                    )
            catch_all = [route for route in enabled if not route.paths]
            if len(catch_all) > 1:
                names = ", ".join(sorted(route.name for route in catch_all))
                raise ValueError(f"Host {host} has multiple catch-all routes: {names}.")
            claimed_paths: dict[str, str] = {}
            for route in enabled:
                for path in route.paths:
                    if path in claimed_paths:
                        raise ValueError(
                            f"Path {path} on {host} is used by both {claimed_paths[path]} and {route.name}."
                        )
                    claimed_paths[path] = route.name
            sorted_routes = sorted(
                host_routes,
                key=lambda route: (
                    not bool(route.paths),
                    -max((len(path) for path in route.paths), default=0),
                    route.name.lower(),
                ),
            )
            tls_lines: list[str] = []
            domain = sorted_routes[0].domain
            provider = next(
                (
                    item
                    for item in providers
                    if domain in item.get("domains", []) and item.get("type") == "netcup"
                ),
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
            content[f"site-{digest}.caddy"] = render_hardened_site(host, sorted_routes, tls_lines)
        return content
