from __future__ import annotations

import json
import re
import uuid
from dataclasses import asdict, dataclass, field
from enum import StrEnum
from typing import Any


SLUG_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,47}$")
HOST_RE = re.compile(
    r"^(?:\*\.)?(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*"
    r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$"
)
UPSTREAM_RE = re.compile(r"^(?:https?://)?[A-Za-z0-9_.:-]+$")
HEADER_RE = re.compile(r"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")
DURATION_RE = re.compile(r"^[1-9]\d*(?:ms|s|m|h)$")
REFERENCE_RE = re.compile(r"^[A-Za-z0-9-]{1,64}$")


class Role(StrEnum):
    ADMIN = "admin"
    EDITOR = "editor"
    VIEWER = "viewer"


class RouteKind(StrEnum):
    PROXY = "proxy"
    REDIRECT = "redirect"
    CUSTOM = "custom"


class Permission(StrEnum):
    VIEW = "view"
    MANAGE_ROUTES = "manage_routes"
    MANAGE_DNS = "manage_dns"
    MANAGE_ACCESS = "manage_access"
    OPERATE_CADDY = "operate_caddy"
    MANAGE_USERS = "manage_users"
    MANAGE_SETTINGS = "manage_settings"
    MANAGE_CUSTOM_ROUTES = "manage_custom_routes"
    RESTORE_BACKUP = "restore_backup"


ROLE_PERMISSIONS: dict[Role, frozenset[Permission]] = {
    Role.ADMIN: frozenset(Permission),
    Role.EDITOR: frozenset(
        {
            Permission.VIEW,
            Permission.MANAGE_ROUTES,
            Permission.MANAGE_DNS,
            Permission.MANAGE_ACCESS,
            Permission.OPERATE_CADDY,
        }
    ),
    Role.VIEWER: frozenset({Permission.VIEW}),
}


@dataclass(slots=True)
class Upstream:
    address: str
    weight: int = 1

    def validate(self) -> None:
        if not UPSTREAM_RE.fullmatch(self.address.strip()):
            raise ValueError(f"Invalid upstream: {self.address}")
        if not 1 <= self.weight <= 100:
            raise ValueError("Upstream weight must be between 1 and 100.")


@dataclass(slots=True)
class HeaderOperation:
    name: str
    value: str
    operation: str = "set"

    def validate(self) -> None:
        if not HEADER_RE.fullmatch(self.name):
            raise ValueError(f"Invalid header name: {self.name}")
        if self.operation not in {"set", "add", "delete"}:
            raise ValueError("Header operation must be set, add, or delete.")
        if "\r" in self.value or "\n" in self.value:
            raise ValueError("Header values cannot contain line breaks.")


@dataclass(slots=True)
class ManagedRoute:
    id: str = field(default_factory=lambda: str(uuid.uuid4()))
    name: str = ""
    domain: str = ""
    host: str = ""
    kind: RouteKind = RouteKind.PROXY
    enabled: bool = True
    paths: list[str] = field(default_factory=list)
    upstreams: list[Upstream] = field(default_factory=list)
    request_headers: list[HeaderOperation] = field(default_factory=list)
    response_headers: list[HeaderOperation] = field(default_factory=list)
    load_balancing: str = "random"
    health_uri: str = ""
    health_interval: str = "30s"
    tls_skip_verify: bool = False
    redirect_to: str = ""
    redirect_status: int = 308
    access_group_id: str = ""
    custom_snippet: str = ""
    created_at: str = ""
    updated_at: str = ""

    @property
    def effective_host(self) -> str:
        if self.host:
            return self.host.strip().rstrip(".")
        if not self.domain:
            raise ValueError("A domain is required when host is empty.")
        return f"{self.name}.{self.domain}".strip().rstrip(".")

    def validate(self) -> None:
        if not SLUG_RE.fullmatch(self.name):
            raise ValueError("Route name must use letters, numbers, dashes, or underscores.")
        if not HOST_RE.fullmatch(self.effective_host):
            raise ValueError("Route host is invalid.")
        if self.kind == RouteKind.PROXY and not self.upstreams:
            raise ValueError("Proxy routes require at least one upstream.")
        if self.kind == RouteKind.REDIRECT and not self.redirect_to.strip():
            raise ValueError("Redirect routes require a destination.")
        if self.kind == RouteKind.CUSTOM and not self.custom_snippet.strip():
            raise ValueError("Custom routes require a snippet.")
        if self.kind != RouteKind.CUSTOM and self.custom_snippet:
            raise ValueError("Only Custom Routes may contain a custom snippet.")
        for upstream in self.upstreams:
            upstream.validate()
        for header in self.request_headers + self.response_headers:
            header.validate()
        if self.load_balancing not in {"random", "round_robin", "least_conn", "first", "ip_hash"}:
            raise ValueError("Unsupported load-balancing policy.")
        if not DURATION_RE.fullmatch(self.health_interval):
            raise ValueError("Health interval must be a positive duration such as 30s or 5m.")
        if self.health_uri and (not self.health_uri.startswith("/") or any(character in self.health_uri for character in "\r\n{}")):
            raise ValueError("Health URI must be a safe absolute path.")
        if self.access_group_id and not REFERENCE_RE.fullmatch(self.access_group_id):
            raise ValueError("Access group reference is invalid.")
        if self.redirect_status not in {301, 302, 303, 307, 308}:
            raise ValueError("Unsupported redirect status.")
        for path in self.paths:
            if not path.startswith("/") or any(character in path for character in "\r\n{}"):
                raise ValueError(f"Invalid path matcher: {path}")
        if any(character in self.custom_snippet for character in "\x00"):
            raise ValueError("Custom snippets contain unsupported characters.")

    def to_json(self) -> str:
        data = asdict(self)
        data["kind"] = self.kind.value
        return json.dumps(data, separators=(",", ":"), sort_keys=True)

    @classmethod
    def from_json(cls, raw: str | dict[str, Any]) -> "ManagedRoute":
        data = json.loads(raw) if isinstance(raw, str) else dict(raw)
        data["kind"] = RouteKind(data.get("kind", RouteKind.PROXY))
        data["upstreams"] = [Upstream(**item) for item in data.get("upstreams", [])]
        data["request_headers"] = [HeaderOperation(**item) for item in data.get("request_headers", [])]
        data["response_headers"] = [HeaderOperation(**item) for item in data.get("response_headers", [])]
        return cls(**data)


@dataclass(slots=True)
class AccessGroup:
    id: str = field(default_factory=lambda: str(uuid.uuid4()))
    name: str = ""
    title: str = "Sign in"
    help_text: str = ""
    accent: str = "#0f6cbd"
    logo_data: str = ""

    def validate(self) -> None:
        if not self.name.strip() or len(self.name) > 80:
            raise ValueError("Access group name is required and must be at most 80 characters.")
        if not re.fullmatch(r"#[0-9A-Fa-f]{6}", self.accent):
            raise ValueError("Accent must be a six-digit hex color.")
        if len(self.logo_data) > 350_000:
            raise ValueError("Portal logo must be smaller than 256 KiB.")
        if self.logo_data and not (
            self.logo_data.startswith("https://")
            or re.fullmatch(r"data:image/(?:png|jpeg|webp|svg\+xml);base64,[A-Za-z0-9+/=]+", self.logo_data)
        ):
            raise ValueError("Portal logo must be an HTTPS URL or a supported image data URL.")
