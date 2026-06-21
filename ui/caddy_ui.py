#!/usr/bin/env python3
import base64
import datetime as dt
import html
import json
import os
import re
import secrets
import shutil
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


HOST = os.getenv("UI_HOST", "0.0.0.0")
PORT = int(os.getenv("UI_PORT", "8098"))
DOMAIN = os.getenv("DOMAIN", "").strip().rstrip(".")
CADDYFILE_PATH = Path(os.getenv("CADDYFILE_PATH", "/etc/caddy/Caddyfile"))
ROUTES_DIR = Path(os.getenv("CADDY_ROUTES_DIR", "/etc/caddy/routes"))
CADDY_DATA_PATH = Path(os.getenv("CADDY_DATA_PATH", "/data"))
CADDY_LOG_PATH = Path(os.getenv("CADDY_LOG_PATH", "/var/log/caddy/access.log"))
CADDY_UI_CONFIG_PATH = Path(os.getenv("CADDY_UI_CONFIG_PATH", "/etc/caddy/caddy-ui.json"))
CADDY_ADMIN_URL = os.getenv("CADDY_ADMIN_URL", "http://caddy:2019").rstrip("/")
AUTO_RELOAD = os.getenv("CADDY_AUTO_RELOAD", "true").lower() in {"1", "true", "yes"}
USERNAME = os.getenv("CADDY_UI_USERNAME", "admin")
PASSWORD = os.getenv("CADDY_UI_PASSWORD", "")
CSRF_TOKEN = secrets.token_urlsafe(32)
SESSION_COOKIE = "caddy_ui_session"
SESSION_TTL_SECONDS = int(os.getenv("CADDY_UI_SESSION_TTL", "86400"))
SESSIONS: dict[str, float] = {}

ROUTE_NAME_RE = re.compile(r"^[A-Za-z0-9_-]{1,48}$")
DNS_LABEL_RE = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")
HOST_RE = re.compile(r"^[A-Za-z0-9*_.-]{1,253}$")
UPSTREAM_RE = re.compile(r"^(https?://)?[A-Za-z0-9_.:-]+$")
META_PREFIX = "# caddy-ui-route:"
DNS_TYPES = ("A", "AAAA", "CNAME", "MX", "TXT", "SRV", "CAA", "NS")


@dataclass
class Route:
    name: str
    host: str
    upstream: str
    tls_skip_verify: bool = False
    basic_auth_user: str = ""
    basic_auth_hash: str = ""

    @property
    def filename(self) -> str:
        return f"{self.name}.caddy"

    @property
    def effective_host(self) -> str:
        return effective_host_for(self.name, self.host)


def route_path(name: str) -> Path:
    validate_name(name)
    return ROUTES_DIR / f"{name}.caddy"


def validate_name(name: str) -> None:
    if not ROUTE_NAME_RE.match(name):
        raise ValueError("Name may only contain letters, numbers, underscores and dashes.")


def effective_host_for(name: str, host: str) -> str:
    host = host.strip()
    if host:
        return host
    domain = current_domain()
    if not domain:
        raise ValueError("Host is required when DOMAIN is not set.")
    if not DNS_LABEL_RE.match(name):
        raise ValueError("Host is required when the route name is not a valid DNS label.")
    return f"{name}.{domain}"


def validate_route(route: Route) -> None:
    validate_name(route.name)
    host = route.effective_host
    if not HOST_RE.match(host) or ".." in host:
        raise ValueError("Host is invalid.")
    if not UPSTREAM_RE.match(route.upstream):
        raise ValueError("Upstream is invalid. Use values like app.internal:5055 or https://app.internal:9443.")
    if route.basic_auth_user and not re.match(r"^[A-Za-z0-9_.@-]{1,80}$", route.basic_auth_user):
        raise ValueError("Basic auth username contains unsupported characters.")


def render_route(route: Route) -> str:
    metadata = json.dumps(
        {
            "name": route.name,
            "host": route.host,
            "upstream": route.upstream,
            "tls_skip_verify": route.tls_skip_verify,
            "basic_auth_user": route.basic_auth_user,
            "basic_auth_hash": route.basic_auth_hash,
        },
        sort_keys=True,
    )
    host = route.effective_host
    lines = [
        "# managed-by caddy-ui",
        f"{META_PREFIX} {metadata}",
        f"@{route.name} host {host}",
        f"handle @{route.name} {{",
    ]
    if route.basic_auth_user and route.basic_auth_hash:
        lines.extend(
            [
                "    basic_auth {",
                f"        {route.basic_auth_user} {route.basic_auth_hash}",
                "    }",
            ]
        )
    if route.upstream.startswith("https://") and route.tls_skip_verify:
        lines.extend(
            [
                f"    reverse_proxy {route.upstream} {{",
                "        transport http {",
                "            tls_insecure_skip_verify",
                "        }",
                "    }",
            ]
        )
    else:
        lines.append(f"    reverse_proxy {route.upstream}")
    lines.append("}")
    lines.append("")
    return "\n".join(lines)


def parse_route_file(path: Path) -> Route | None:
    try:
        content = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        return None
    for line in content.splitlines():
        if line.startswith(META_PREFIX):
            raw = line[len(META_PREFIX) :].strip()
            data = json.loads(raw)
            return Route(
                name=str(data["name"]),
                host=str(data.get("host", "")),
                upstream=str(data["upstream"]),
                tls_skip_verify=bool(data.get("tls_skip_verify", False)),
                basic_auth_user=str(data.get("basic_auth_user", "")),
                basic_auth_hash=str(data.get("basic_auth_hash", "")),
            )
    return None


def list_routes() -> list[Route]:
    ROUTES_DIR.mkdir(parents=True, exist_ok=True)
    routes = []
    for path in sorted(ROUTES_DIR.glob("*.caddy")):
        route = parse_route_file(path)
        if route:
            routes.append(route)
    return routes


def save_route(route: Route) -> None:
    validate_route(route)
    ROUTES_DIR.mkdir(parents=True, exist_ok=True)
    path = route_path(route.name)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(render_route(route), encoding="utf-8", newline="\n")
    tmp.replace(path)


def hash_basic_auth_password(password: str) -> str:
    result = subprocess.run(
        ["/usr/bin/caddy", "hash-password", "--plaintext", password],
        check=True,
        capture_output=True,
        text=True,
        timeout=15,
    )
    return result.stdout.strip()


def delete_route(name: str) -> None:
    path = route_path(name)
    path.unlink(missing_ok=True)


def request_caddy_get(path: str) -> tuple[int, str]:
    request = urllib.request.Request(
        f"{CADDY_ADMIN_URL}{path}",
        headers={"User-Agent": "caddy-ui/1.0"},
        method="GET",
    )
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status, response.read(4096).decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read(4096).decode("utf-8", errors="replace")
    except Exception as exc:
        return 0, str(exc)


def request_caddy(path: str, body: bytes) -> tuple[int, str]:
    request = urllib.request.Request(
        f"{CADDY_ADMIN_URL}{path}",
        data=body,
        headers={
            "Content-Type": "text/caddyfile",
            "Cache-Control": "must-revalidate",
            "User-Agent": "caddy-ui/1.0",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            return response.status, response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", errors="replace")


def reload_caddy() -> None:
    caddyfile = CADDYFILE_PATH.read_bytes()
    adapt_status, adapt_body = request_caddy("/adapt", caddyfile)
    if adapt_status >= 300:
        raise RuntimeError(f"Caddyfile validation failed: {adapt_body}")
    load_status, load_body = request_caddy("/load", caddyfile)
    if load_status >= 300:
        raise RuntimeError(f"Caddy reload failed: {load_body}")


def parse_cert_time(value: str) -> dt.datetime | None:
    if not value:
        return None
    try:
        return dt.datetime.strptime(value, "%b %d %H:%M:%S %Y %Z").replace(tzinfo=dt.timezone.utc)
    except ValueError:
        return None


def certificate_subject_name(decoded: dict) -> str:
    subject = decoded.get("subject", ())
    for part in subject:
        for key, value in part:
            if key == "commonName":
                return str(value)
    return ""


def list_certificates() -> list[dict]:
    if not CADDY_DATA_PATH.exists():
        return []
    certificates = []
    for path in sorted(CADDY_DATA_PATH.rglob("*.crt")):
        try:
            decoded = ssl._ssl._test_decode_cert(str(path))
        except Exception as exc:
            certificates.append(
                {
                    "path": str(path),
                    "error": str(exc),
                    "subject": path.stem,
                    "sans": [],
                    "expires_at": "",
                    "days_remaining": None,
                    "wildcard": False,
                }
            )
            continue

        sans = [
            value
            for kind, value in decoded.get("subjectAltName", ())
            if kind.lower() == "dns"
        ]
        expires_at = parse_cert_time(str(decoded.get("notAfter", "")))
        days_remaining = None
        if expires_at:
            delta = expires_at - dt.datetime.now(dt.timezone.utc)
            days_remaining = delta.days
        certificates.append(
            {
                "path": str(path),
                "subject": certificate_subject_name(decoded) or path.stem,
                "issuer": decoded.get("issuer", ()),
                "sans": sans,
                "expires_at": expires_at.isoformat() if expires_at else str(decoded.get("notAfter", "")),
                "days_remaining": days_remaining,
                "wildcard": any(san.startswith("*.") for san in sans),
            }
        )
    return certificates


def tail_lines(path: Path, max_bytes: int = 512 * 1024) -> list[str]:
    if not path.exists() or not path.is_file():
        return []
    size = path.stat().st_size
    with path.open("rb") as handle:
        if size > max_bytes:
            handle.seek(-max_bytes, os.SEEK_END)
            handle.readline()
        data = handle.read()
    return data.decode("utf-8", errors="replace").splitlines()


def increment(counter: dict[str, int], key: str) -> None:
    counter[key] = counter.get(key, 0) + 1


def top_items(counter: dict[str, int], limit: int = 8) -> list[dict]:
    return [
        {"value": key, "count": count}
        for key, count in sorted(counter.items(), key=lambda item: item[1], reverse=True)[:limit]
    ]


def collect_access_stats() -> dict:
    hosts: dict[str, int] = {}
    paths: dict[str, int] = {}
    statuses: dict[str, int] = {}
    recent = []
    total = 0
    for line in tail_lines(CADDY_LOG_PATH):
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            continue
        request = entry.get("request", {}) if isinstance(entry.get("request"), dict) else {}
        host = str(request.get("host") or "unknown")
        uri = str(request.get("uri") or "/")
        path = urllib.parse.urlparse(uri).path or "/"
        status = str(entry.get("status") or "unknown")
        increment(hosts, host)
        increment(paths, path)
        increment(statuses, status)
        total += 1
        recent.append(
            {
                "ts": entry.get("ts", ""),
                "host": host,
                "path": path,
                "status": status,
                "method": request.get("method", ""),
                "remote_ip": request.get("remote_ip", ""),
            }
        )
    return {
        "log_path": str(CADDY_LOG_PATH),
        "total_sampled": total,
        "top_hosts": top_items(hosts),
        "top_paths": top_items(paths),
        "statuses": top_items(statuses, 12),
        "recent": recent[-12:],
    }


def collect_status() -> dict:
    routes = list_routes()
    certificates = list_certificates()
    access_stats = collect_access_stats()
    admin_status, admin_body = request_caddy_get("/config/")
    return {
        "domain": current_domain(),
        "caddy_admin_url": CADDY_ADMIN_URL,
        "caddy_admin_status": admin_status,
        "caddy_admin_ok": 200 <= admin_status < 300,
        "caddy_admin_error": "" if 200 <= admin_status < 300 else admin_body,
        "caddyfile_path": str(CADDYFILE_PATH),
        "routes_dir": str(ROUTES_DIR),
        "caddy_data_path": str(CADDY_DATA_PATH),
        "caddy_log_path": str(CADDY_LOG_PATH),
        "auto_reload": AUTO_RELOAD,
        "route_count": len(routes),
        "certificate_count": len(certificates),
        "wildcard_certificate_count": sum(1 for cert in certificates if cert.get("wildcard")),
        "certificates": certificates,
        "access_stats": access_stats,
    }


def expand_env(value: str) -> str:
    value = str(value or "")
    match = re.fullmatch(r"\{env\.([A-Za-z_][A-Za-z0-9_]*)\}", value)
    if match:
        return os.getenv(match.group(1), "")
    return value


def default_provider_config() -> dict:
    domains = [DOMAIN] if DOMAIN else []
    return {
        "settings": {
            "domain": DOMAIN,
        },
        "providers": [
            {
                "id": "netcup-default",
                "type": "netcup",
                "label": "Netcup",
                "customer_number": "{env.NETCUP_CUSTOMER_NUMBER}",
                "api_key": "{env.NETCUP_API_KEY}",
                "api_password": "{env.NETCUP_API_PASSWORD}",
                "domains": domains,
            }
        ]
    }


def current_domain() -> str:
    settings = read_ui_config().get("settings", {})
    if isinstance(settings, dict):
        domain = str(settings.get("domain") or "").strip().rstrip(".")
        if domain:
            return domain
    return DOMAIN


def read_ui_config() -> dict:
    if not CADDY_UI_CONFIG_PATH.exists():
        return default_provider_config()
    try:
        data = json.loads(CADDY_UI_CONFIG_PATH.read_text(encoding="utf-8"))
    except Exception:
        return default_provider_config()
    if not isinstance(data, dict):
        return default_provider_config()
    providers = data.get("providers")
    if not isinstance(providers, list):
        data["providers"] = []
    if not isinstance(data.get("settings"), dict):
        data["settings"] = {"domain": DOMAIN}
    return data


def write_ui_config(data: dict) -> None:
    CADDY_UI_CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp = CADDY_UI_CONFIG_PATH.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, indent=2, sort_keys=True), encoding="utf-8", newline="\n")
    tmp.replace(CADDY_UI_CONFIG_PATH)


def list_providers() -> list[dict]:
    providers = []
    for provider in read_ui_config().get("providers", []):
        if not isinstance(provider, dict):
            continue
        provider_id = str(provider.get("id", "")).strip()
        provider_type = str(provider.get("type", "")).strip()
        if not provider_id or not provider_type:
            continue
        normalized = dict(provider)
        normalized["id"] = provider_id
        normalized["type"] = provider_type
        normalized["label"] = str(provider.get("label") or provider_id)
        domains = provider.get("domains") or []
        if isinstance(domains, str):
            domains = [domain.strip() for domain in domains.split(",") if domain.strip()]
        normalized["domains"] = [str(domain).strip().rstrip(".") for domain in domains if str(domain).strip()]
        providers.append(normalized)
    return providers


def provider_public(provider: dict) -> dict:
    return {
        "id": provider.get("id", ""),
        "type": provider.get("type", ""),
        "label": provider.get("label", ""),
        "domains": provider.get("domains", []),
    }


def get_provider(provider_id: str) -> dict:
    for provider in list_providers():
        if provider["id"] == provider_id:
            return provider
    raise ValueError("Provider account not found.")


def save_provider(provider: dict) -> None:
    provider_id = str(provider.get("id", "")).strip()
    if not re.match(r"^[A-Za-z0-9_-]{1,48}$", provider_id):
        raise ValueError("Provider ID may only contain letters, numbers, underscores and dashes.")
    provider_type = str(provider.get("type", "")).strip().lower()
    if provider_type != "netcup":
        raise ValueError("Only netcup providers are implemented right now.")
    data = read_ui_config()
    providers = [item for item in data.get("providers", []) if isinstance(item, dict) and item.get("id") != provider_id]
    providers.append(provider)
    data["providers"] = providers
    write_ui_config(data)


def delete_provider(provider_id: str) -> None:
    data = read_ui_config()
    data["providers"] = [
        item for item in data.get("providers", []) if not (isinstance(item, dict) and item.get("id") == provider_id)
    ]
    write_ui_config(data)


def netcup_client(provider: dict):
    from ddns.netcup_ddns import NetcupClient

    return NetcupClient(
        expand_env(provider.get("customer_number", "")),
        expand_env(provider.get("api_key", "")),
        expand_env(provider.get("api_password", "")),
    )


def dns_record_from_form(form: dict[str, list[str]], delete: bool = False) -> dict:
    record = {
        "id": form.get("id", [""])[0].strip(),
        "hostname": form.get("hostname", [""])[0].strip() or "@",
        "type": form.get("type", ["A"])[0].strip().upper(),
        "destination": form.get("destination", [""])[0].strip(),
    }
    priority = form.get("priority", [""])[0].strip()
    if priority:
        record["priority"] = priority
    if delete:
        record["deleterecord"] = True
    if record["type"] not in DNS_TYPES:
        raise ValueError("Unsupported DNS record type.")
    if not record["destination"] and not delete:
        raise ValueError("DNS record destination is required.")
    return record


def netcup_records(provider_id: str, domain: str) -> list[dict]:
    provider = get_provider(provider_id)
    if provider["type"] != "netcup":
        raise ValueError("Only netcup providers are implemented right now.")
    domain = domain.strip().rstrip(".")
    if not domain:
        raise ValueError("Domain is required.")
    client = netcup_client(provider)
    client.login()
    try:
        return client.dns_records(domain)
    finally:
        client.logout()


def netcup_update_records(provider_id: str, domain: str, records: list[dict]) -> None:
    provider = get_provider(provider_id)
    if provider["type"] != "netcup":
        raise ValueError("Only netcup providers are implemented right now.")
    client = netcup_client(provider)
    client.login()
    try:
        client.update_dns_records(domain.strip().rstrip("."), records)
    finally:
        client.logout()


def change_with_rollback(change) -> None:
    ROUTES_DIR.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp_dir:
        backup_dir = Path(tmp_dir) / "routes"
        if ROUTES_DIR.exists():
            shutil.copytree(ROUTES_DIR, backup_dir)
        change()
        if AUTO_RELOAD:
            try:
                reload_caddy()
            except Exception:
                if backup_dir.exists():
                    shutil.rmtree(ROUTES_DIR, ignore_errors=True)
                    shutil.copytree(backup_dir, ROUTES_DIR)
                raise


def escape(value: object) -> str:
    return html.escape(str(value), quote=True)


def parse_cookies(header: str) -> dict[str, str]:
    cookies = {}
    for part in header.split(";"):
        if "=" not in part:
            continue
        key, value = part.split("=", 1)
        cookies[key.strip()] = value.strip()
    return cookies


def create_session() -> str:
    token = secrets.token_urlsafe(32)
    SESSIONS[token] = time.time() + SESSION_TTL_SECONDS
    return token


def valid_session(token: str) -> bool:
    expires_at = SESSIONS.get(token)
    if not expires_at:
        return False
    if expires_at < time.time():
        SESSIONS.pop(token, None)
        return False
    return True


def display_host(route: Route) -> tuple[str, bool]:
    if route.host.strip():
        return route.host.strip(), False
    return route.effective_host, True


def page(title: str, body: str, message: str = "", error: str = "") -> bytes:
    message_html = f'<div class="notice">{escape(message)}</div>' if message else ""
    error_html = f'<div class="error">{escape(error)}</div>' if error else ""
    html_doc = f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{escape(title)}</title>
  <style>
    :root {{
      color-scheme: light;
      --bg: #f6f7f8;
      --panel: #ffffff;
      --text: #171717;
      --muted: #666f76;
      --line: #d9dee3;
      --accent: #107c41;
      --danger: #b42318;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      font: 14px/1.45 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      background: var(--bg);
      color: var(--text);
    }}
    main {{
      width: min(1180px, calc(100vw - 32px));
      margin: 24px auto 48px;
    }}
    header {{
      display: flex;
      justify-content: space-between;
      align-items: flex-end;
      gap: 16px;
      margin-bottom: 18px;
    }}
    h1 {{ font-size: 24px; margin: 0; }}
    h2 {{ font-size: 17px; margin: 0 0 12px; }}
    .muted {{ color: var(--muted); }}
    .grid {{
      display: grid;
      grid-template-columns: minmax(0, 1fr) 360px;
      gap: 18px;
      align-items: start;
    }}
    .status-grid {{
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 10px;
      margin-bottom: 18px;
    }}
    .stat {{
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 12px;
      min-width: 0;
    }}
    .stat strong {{
      display: block;
      font-size: 20px;
      line-height: 1.1;
      margin-top: 4px;
    }}
    .pill {{
      display: inline-flex;
      align-items: center;
      border-radius: 999px;
      padding: 3px 8px;
      font-size: 12px;
      font-weight: 700;
    }}
    .ok {{ background: #ecf8f0; color: #0f6b3b; }}
    .bad {{ background: #fff0ee; color: var(--danger); }}
    .warn {{ background: #fff7e6; color: #8a5a00; }}
    .cert-grid {{
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
      gap: 10px;
    }}
    .cert {{
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 10px;
      min-width: 0;
    }}
    .cert code, td code {{ overflow-wrap: anywhere; }}
    section {{
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 16px;
    }}
    table {{
      width: 100%;
      border-collapse: collapse;
    }}
    th, td {{
      padding: 10px 8px;
      border-bottom: 1px solid var(--line);
      text-align: left;
      vertical-align: middle;
    }}
    th {{
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: .04em;
      color: var(--muted);
    }}
    tr:last-child td {{ border-bottom: 0; }}
    code {{
      background: #edf0f2;
      border-radius: 5px;
      padding: 2px 5px;
      font-size: 13px;
    }}
    form.stack {{ display: grid; gap: 10px; }}
    label {{ display: grid; gap: 5px; font-weight: 600; }}
    input[type="text"], input[type="url"], input[type="password"], select {{
      width: 100%;
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 9px 10px;
      font: inherit;
      background: white;
    }}
    .row {{
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }}
    button, .button {{
      border: 0;
      border-radius: 6px;
      padding: 9px 12px;
      font: inherit;
      font-weight: 700;
      background: var(--accent);
      color: white;
      cursor: pointer;
      text-decoration: none;
      display: inline-flex;
      align-items: center;
      justify-content: center;
    }}
    button.secondary {{ background: #505a62; }}
    button.danger {{ background: var(--danger); }}
    .inline {{ display: inline; }}
    .notice, .error {{
      border-radius: 8px;
      margin-bottom: 14px;
      padding: 10px 12px;
      border: 1px solid;
    }}
    .notice {{ background: #ecf8f0; border-color: #9ed8b5; }}
    .error {{ background: #fff0ee; border-color: #f0a29a; }}
    @media (max-width: 900px) {{
      .grid, .status-grid {{ grid-template-columns: 1fr; }}
      header {{ align-items: flex-start; flex-direction: column; }}
      table {{ display: block; overflow-x: auto; }}
    }}
  </style>
</head>
<body>
<main>
  <header>
    <div>
      <h1>Caddy UI</h1>
      <div class="muted">Managed reverse proxy routes in <code>{escape(str(ROUTES_DIR))}</code></div>
    </div>
    <div class="row">
      <a class="button secondary" href="/">Routes</a>
      <a class="button secondary" href="/dns">DNS</a>
      <a class="button secondary" href="/settings">Settings</a>
      <form method="post" action="/reload" class="inline">
        <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
        <button class="secondary" type="submit">Reload</button>
      </form>
      <form method="post" action="/logout" class="inline">
        <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
        <button class="secondary" type="submit">Logout</button>
      </form>
    </div>
  </header>
  {message_html}
  {error_html}
  {body}
</main>
</body>
</html>"""
    return html_doc.encode("utf-8")


def render_login(error: str = "") -> bytes:
    error_html = f'<div class="error">{escape(error)}</div>' if error else ""
    html_doc = f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Caddy UI Login</title>
  <style>
    :root {{
      color-scheme: light;
      --bg: #f6f7f8;
      --panel: #ffffff;
      --text: #171717;
      --muted: #666f76;
      --line: #d9dee3;
      --accent: #107c41;
      --danger: #b42318;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      min-height: 100vh;
      display: grid;
      place-items: center;
      font: 14px/1.45 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      background: var(--bg);
      color: var(--text);
    }}
    main {{
      width: min(380px, calc(100vw - 32px));
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 18px;
    }}
    h1 {{ font-size: 24px; margin: 0 0 4px; }}
    .muted {{ color: var(--muted); margin-bottom: 16px; }}
    form {{ display: grid; gap: 10px; }}
    label {{ display: grid; gap: 5px; font-weight: 600; }}
    input {{
      width: 100%;
      border: 1px solid var(--line);
      border-radius: 6px;
      padding: 9px 10px;
      font: inherit;
      background: white;
    }}
    button {{
      border: 0;
      border-radius: 6px;
      padding: 9px 12px;
      font: inherit;
      font-weight: 700;
      background: var(--accent);
      color: white;
      cursor: pointer;
    }}
    .error {{
      border-radius: 8px;
      margin-bottom: 14px;
      padding: 10px 12px;
      border: 1px solid #f0a29a;
      background: #fff0ee;
    }}
  </style>
</head>
<body>
<main>
  <h1>Caddy UI</h1>
  <div class="muted">Sign in to manage routes and status.</div>
  {error_html}
  <form method="post" action="/login">
    <label>Username
      <input name="username" type="text" autocomplete="username" required>
    </label>
    <label>Password
      <input name="password" type="password" autocomplete="current-password" required>
    </label>
    <button type="submit">Sign in</button>
  </form>
</main>
</body>
</html>"""
    return html_doc.encode("utf-8")


def render_status(status: dict) -> str:
    admin_class = "ok" if status["caddy_admin_ok"] else "bad"
    admin_text = "online" if status["caddy_admin_ok"] else "offline"
    wildcard_text = str(status["wildcard_certificate_count"])
    cert_rows = ""
    if status["certificates"]:
        cards = []
        for cert in status["certificates"]:
            days = cert.get("days_remaining")
            if days is None:
                days_label = "unknown"
                days_class = "warn"
            elif days < 14:
                days_label = f"{days} days"
                days_class = "bad"
            elif days < 30:
                days_label = f"{days} days"
                days_class = "warn"
            else:
                days_label = f"{days} days"
                days_class = "ok"
            wildcard = '<span class="pill ok">wildcard</span>' if cert.get("wildcard") else ""
            sans = ", ".join(cert.get("sans") or [])
            if not sans:
                sans = cert.get("subject", "")
            error = cert.get("error", "")
            error_html = f'<div class="muted">Error: {escape(error)}</div>' if error else ""
            cards.append(
                f"""<div class="cert">
  <div class="row"><strong>{escape(cert.get("subject", ""))}</strong>{wildcard}<span class="pill {days_class}">{escape(days_label)}</span></div>
  <div class="muted">Names: <code>{escape(sans)}</code></div>
  <div class="muted">Expires: <code>{escape(cert.get("expires_at", ""))}</code></div>
  <div class="muted">File: <code>{escape(cert.get("path", ""))}</code></div>
  {error_html}
</div>"""
            )
        cert_rows = "\n".join(cards)
    else:
        cert_rows = '<div class="muted">No certificate files found in the configured Caddy data path yet.</div>'

    admin_error = ""
    if not status["caddy_admin_ok"] and status.get("caddy_admin_error"):
        admin_error = f'<div class="error">Caddy admin error: {escape(status["caddy_admin_error"])}</div>'
    access_stats = status.get("access_stats", {})

    def render_count_list(items: list[dict]) -> str:
        if not items:
            return '<div class="muted">No data.</div>'
        return "".join(
            f'<div class="row"><code>{escape(item.get("value", ""))}</code><span class="muted">{escape(item.get("count", 0))}</span></div>'
            for item in items
        )

    recent_rows = ""
    for item in access_stats.get("recent", []):
        recent_rows += (
            f"<tr><td><code>{escape(item.get('status', ''))}</code></td>"
            f"<td>{escape(item.get('method', ''))}</td>"
            f"<td><code>{escape(item.get('host', ''))}</code></td>"
            f"<td><code>{escape(item.get('path', ''))}</code></td></tr>"
        )
    if not recent_rows:
        recent_rows = '<tr><td colspan="4" class="muted">No recent requests in sampled log.</td></tr>'

    return f"""
<div class="status-grid">
  <div class="stat"><span class="muted">Caddy Admin</span><strong><span class="pill {admin_class}">{admin_text}</span></strong></div>
  <div class="stat"><span class="muted">Routes</span><strong>{escape(status["route_count"])}</strong></div>
  <div class="stat"><span class="muted">Certificates</span><strong>{escape(status["certificate_count"])}</strong></div>
  <div class="stat"><span class="muted">Sampled Requests</span><strong>{escape(access_stats.get("total_sampled", 0))}</strong></div>
</div>
{admin_error}
<section>
  <h2>Status</h2>
  <table>
    <tbody>
      <tr><th>Domain</th><td><code>{escape(status["domain"] or "not set")}</code></td></tr>
      <tr><th>Caddy Admin URL</th><td><code>{escape(status["caddy_admin_url"])}</code></td></tr>
      <tr><th>Caddyfile</th><td><code>{escape(status["caddyfile_path"])}</code></td></tr>
      <tr><th>Routes Directory</th><td><code>{escape(status["routes_dir"])}</code></td></tr>
      <tr><th>Caddy Data Path</th><td><code>{escape(status["caddy_data_path"])}</code></td></tr>
      <tr><th>Caddy Log Path</th><td><code>{escape(status["caddy_log_path"])}</code></td></tr>
      <tr><th>Auto Reload</th><td>{'enabled' if status["auto_reload"] else 'disabled'}</td></tr>
      <tr><th>Wildcard Certs</th><td>{escape(wildcard_text)}</td></tr>
    </tbody>
  </table>
</section>
<section>
  <h2>Certificates</h2>
  <div class="cert-grid">{cert_rows}</div>
</section>
<section>
  <h2>Access Stats</h2>
  <div class="cert-grid">
    <div class="cert"><strong>Top Hosts</strong>{render_count_list(access_stats.get("top_hosts", []))}</div>
    <div class="cert"><strong>Top Paths</strong>{render_count_list(access_stats.get("top_paths", []))}</div>
    <div class="cert"><strong>Status Codes</strong>{render_count_list(access_stats.get("statuses", []))}</div>
  </div>
  <table>
    <thead><tr><th>Status</th><th>Method</th><th>Host</th><th>Path</th></tr></thead>
    <tbody>{recent_rows}</tbody>
  </table>
</section>
"""


def render_home(message: str = "", error: str = "") -> bytes:
    routes = list_routes()
    status = collect_status()
    if routes:
        row_parts = []
        for route in routes:
            try:
                host, derived = display_host(route)
                host_note = ' <span class="muted">(derived)</span>' if derived else ""
            except ValueError as exc:
                host = str(exc)
                host_note = ""
            row_parts.append(
                f"""<tr>
  <td><strong>{escape(route.name)}</strong></td>
  <td><code>{escape(host)}</code>{host_note}</td>
  <td><code>{escape(route.upstream)}</code></td>
  <td>{'yes' if route.tls_skip_verify else 'no'}</td>
  <td>{'yes' if route.basic_auth_user else 'no'}</td>
  <td>
    <form method="post" action="/routes/delete" class="inline">
      <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
      <input type="hidden" name="name" value="{escape(route.name)}">
      <button class="danger" type="submit">Delete</button>
    </form>
  </td>
</tr>"""
            )
        rows = "\n".join(row_parts)
    else:
        rows = '<tr><td colspan="6" class="muted">No managed routes yet.</td></tr>'

    body = f"""
{render_status(status)}
<div class="grid">
  <section>
    <h2>Routes</h2>
    <table>
      <thead>
        <tr>
          <th>Name</th>
          <th>Host</th>
          <th>Upstream</th>
          <th>Skip TLS Verify</th>
          <th>Basic Auth</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        {rows}
      </tbody>
    </table>
  </section>

  <section>
    <h2>Create or replace route</h2>
    <form method="post" action="/routes" class="stack">
      <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
      <label>Name
        <input name="name" type="text" placeholder="app" required>
      </label>
      <label>Host (optional)
        <input name="host" type="text" placeholder="optional; defaults to name + DOMAIN">
      </label>
      <label>Upstream
        <input name="upstream" type="text" placeholder="app.internal:5055" required>
      </label>
      <label class="row">
        <input name="tls_skip_verify" type="checkbox" value="1">
        Do not verify the upstream TLS certificate
      </label>
      <label>Basic Auth Username
        <input name="basic_auth_user" type="text" placeholder="optional">
      </label>
      <label>Basic Auth Password
        <input name="basic_auth_password" type="password" placeholder="leave empty to disable">
      </label>
      <button type="submit">Save</button>
    </form>
  </section>
</div>
"""
    return page("Caddy UI", body, message, error)


def render_dns_page(message: str = "", error: str = "", provider_id: str = "", domain: str = "") -> bytes:
    providers = list_providers()
    if not provider_id and providers:
        provider_id = providers[0]["id"]
    selected_provider = None
    for provider in providers:
        if provider["id"] == provider_id:
            selected_provider = provider
            break
    if selected_provider and not domain and selected_provider.get("domains"):
        domain = selected_provider["domains"][0]

    provider_options = "\n".join(
        f'<option value="{escape(provider["id"])}" {"selected" if provider["id"] == provider_id else ""}>{escape(provider["label"])} ({escape(provider["type"])})</option>'
        for provider in providers
    )
    domain_options = ""
    if selected_provider:
        domain_options = "\n".join(
            f'<option value="{escape(item)}" {"selected" if item == domain else ""}>{escape(item)}</option>'
            for item in selected_provider.get("domains", [])
        )

    records = []
    records_error = ""
    if selected_provider and domain:
        try:
            records = netcup_records(provider_id, domain)
        except Exception as exc:
            records_error = str(exc)

    type_options = "".join(f'<option value="{record_type}">{record_type}</option>' for record_type in DNS_TYPES)
    rows = []
    for index, record in enumerate(records):
        record_type = str(record.get("type", "A")).upper()
        form_id = f"dns-record-{index}"
        delete_form_id = f"dns-delete-{index}"
        row_type_options = "".join(
            f'<option value="{item}" {"selected" if item == record_type else ""}>{item}</option>' for item in DNS_TYPES
        )
        rows.append(
            f"""<tr>
  <td>
    <form id="{form_id}" method="post" action="/dns/update"></form>
    <form id="{delete_form_id}" method="post" action="/dns/delete"></form>
    <input form="{form_id}" type="hidden" name="csrf" value="{CSRF_TOKEN}">
    <input form="{form_id}" type="hidden" name="provider_id" value="{escape(provider_id)}">
    <input form="{form_id}" type="hidden" name="domain" value="{escape(domain)}">
    <input form="{form_id}" type="hidden" name="id" value="{escape(record.get("id", ""))}">
    <input form="{delete_form_id}" type="hidden" name="csrf" value="{CSRF_TOKEN}">
    <input form="{delete_form_id}" type="hidden" name="provider_id" value="{escape(provider_id)}">
    <input form="{delete_form_id}" type="hidden" name="domain" value="{escape(domain)}">
    <input form="{delete_form_id}" type="hidden" name="id" value="{escape(record.get("id", ""))}">
    <input form="{delete_form_id}" type="hidden" name="hostname" value="{escape(record.get("hostname", ""))}">
    <input form="{delete_form_id}" type="hidden" name="type" value="{escape(record.get("type", ""))}">
    <input form="{delete_form_id}" type="hidden" name="priority" value="{escape(record.get("priority", ""))}">
    <input form="{delete_form_id}" type="hidden" name="destination" value="{escape(record.get("destination", ""))}">
    <input form="{form_id}" name="hostname" type="text" value="{escape(record.get("hostname", ""))}">
  </td>
  <td><select form="{form_id}" name="type">{row_type_options}</select></td>
  <td><input form="{form_id}" name="priority" type="text" value="{escape(record.get("priority", ""))}"></td>
  <td><input form="{form_id}" name="destination" type="text" value="{escape(record.get("destination", ""))}"></td>
  <td class="row">
    <button form="{form_id}" type="submit">Save</button>
    <button form="{delete_form_id}" class="danger" type="submit">Delete</button>
  </td>
</tr>"""
        )
    records_rows = "\n".join(rows) if rows else '<tr><td colspan="5" class="muted">No DNS records loaded.</td></tr>'
    records_error_html = f'<div class="error">{escape(records_error)}</div>' if records_error else ""

    providers_rows = "\n".join(
        f"""<tr>
  <td><strong>{escape(provider["label"])}</strong></td>
  <td>{escape(provider["type"])}</td>
  <td><code>{escape(", ".join(provider.get("domains", [])))}</code></td>
  <td>
    <form method="post" action="/providers/delete" class="inline">
      <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
      <input type="hidden" name="provider_id" value="{escape(provider["id"])}">
      <button class="danger" type="submit">Delete</button>
    </form>
  </td>
</tr>"""
        for provider in providers
    )
    if not providers_rows:
        providers_rows = '<tr><td colspan="4" class="muted">No provider accounts configured.</td></tr>'

    body = f"""
<div class="grid">
  <section>
    <h2>DNS Records</h2>
    <form method="get" action="/dns" class="stack">
      <label>Provider Account
        <select name="provider_id">{provider_options}</select>
      </label>
      <label>Domain
        <input name="domain" type="text" list="dns-domains" value="{escape(domain)}" placeholder="example.com">
        <datalist id="dns-domains">{domain_options}</datalist>
      </label>
      <button type="submit">Load Records</button>
    </form>
    {records_error_html}
    <table>
      <thead>
        <tr><th>Host</th><th>Type</th><th>Priority</th><th>Destination</th><th></th></tr>
      </thead>
      <tbody>{records_rows}</tbody>
    </table>
  </section>

  <section>
    <h2>Add DNS Record</h2>
    <form method="post" action="/dns/add" class="stack">
      <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
      <input type="hidden" name="provider_id" value="{escape(provider_id)}">
      <input type="hidden" name="domain" value="{escape(domain)}">
      <label>Host
        <input name="hostname" type="text" placeholder="@ or app" required>
      </label>
      <label>Type
        <select name="type">{type_options}</select>
      </label>
      <label>Priority
        <input name="priority" type="text" placeholder="MX only">
      </label>
      <label>Destination
        <input name="destination" type="text" required>
      </label>
      <button type="submit">Add Record</button>
    </form>
  </section>
</div>

<section>
  <h2>Provider Accounts</h2>
  <table>
    <thead><tr><th>Label</th><th>Type</th><th>Domains</th><th></th></tr></thead>
    <tbody>{providers_rows}</tbody>
  </table>
</section>

<section>
  <h2>Add or Replace Provider Account</h2>
  <form method="post" action="/providers" class="stack">
    <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
    <label>ID
      <input name="id" type="text" placeholder="netcup-main" required>
    </label>
    <label>Label
      <input name="label" type="text" placeholder="Netcup Main" required>
    </label>
    <label>Type
      <select name="type"><option value="netcup">netcup</option></select>
    </label>
    <label>Domains
      <input name="domains" type="text" placeholder="example.com, example.net">
    </label>
    <label>Netcup Customer Number
      <input name="customer_number" type="text" autocomplete="off" required>
    </label>
    <label>Netcup API Key
      <input name="api_key" type="text" autocomplete="off" required>
    </label>
    <label>Netcup API Password
      <input name="api_password" type="password" autocomplete="new-password" required>
    </label>
    <button type="submit">Save Provider</button>
  </form>
</section>
"""
    return page("Caddy DNS", body, message, error)


def render_settings_page(message: str = "", error: str = "") -> bytes:
    config = read_ui_config()
    settings = config.get("settings", {}) if isinstance(config.get("settings"), dict) else {}
    providers = list_providers()
    providers_rows = "\n".join(
        f"""<tr>
  <td><strong>{escape(provider["label"])}</strong></td>
  <td>{escape(provider["type"])}</td>
  <td><code>{escape(provider["id"])}</code></td>
  <td><code>{escape(", ".join(provider.get("domains", [])))}</code></td>
  <td>
    <form method="post" action="/providers/delete" class="inline">
      <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
      <input type="hidden" name="provider_id" value="{escape(provider["id"])}">
      <button class="danger" type="submit">Delete</button>
    </form>
  </td>
</tr>"""
        for provider in providers
    )
    if not providers_rows:
        providers_rows = '<tr><td colspan="5" class="muted">No provider accounts configured.</td></tr>'

    body = f"""
<div class="grid">
  <section>
    <h2>General Settings</h2>
    <form method="post" action="/settings" class="stack">
      <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
      <label>Default Domain
        <input name="domain" type="text" value="{escape(settings.get("domain", DOMAIN))}" placeholder="example.com">
      </label>
      <button type="submit">Save Settings</button>
    </form>
    <p class="muted">The default domain is used when route host is empty. Caddy certificate site blocks are still generated by the Caddyfile for now.</p>
  </section>

  <section>
    <h2>Add or Replace Provider Account</h2>
    <form method="post" action="/providers" class="stack">
      <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
      <label>ID
        <input name="id" type="text" placeholder="netcup-main" required>
      </label>
      <label>Label
        <input name="label" type="text" placeholder="Netcup Main" required>
      </label>
      <label>Type
        <select name="type"><option value="netcup">netcup</option></select>
      </label>
      <label>Domains
        <input name="domains" type="text" placeholder="example.com, example.net">
      </label>
      <label>Netcup Customer Number
        <input name="customer_number" type="text" autocomplete="off" required>
      </label>
      <label>Netcup API Key
        <input name="api_key" type="text" autocomplete="off" required>
      </label>
      <label>Netcup API Password
        <input name="api_password" type="password" autocomplete="new-password" required>
      </label>
      <button type="submit">Save Provider</button>
    </form>
  </section>
</div>

<section>
  <h2>Provider Accounts</h2>
  <table>
    <thead><tr><th>Label</th><th>Type</th><th>ID</th><th>Domains</th><th></th></tr></thead>
    <tbody>{providers_rows}</tbody>
  </table>
</section>
"""
    return page("Caddy Settings", body, message, error)


class Handler(BaseHTTPRequestHandler):
    server_version = "CaddyUI/1.0"

    def do_GET(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == "/login":
            if self.is_authenticated():
                self.redirect()
                return
            query = urllib.parse.parse_qs(parsed.query)
            self.send_html(render_login(query.get("error", [""])[0]))
            return
        if not self.authorized():
            return
        if parsed.path == "/":
            query = urllib.parse.parse_qs(parsed.query)
            self.send_html(render_home(query.get("message", [""])[0], query.get("error", [""])[0]))
            return
        if parsed.path == "/dns":
            query = urllib.parse.parse_qs(parsed.query)
            self.send_html(
                render_dns_page(
                    query.get("message", [""])[0],
                    query.get("error", [""])[0],
                    query.get("provider_id", [""])[0],
                    query.get("domain", [""])[0],
                )
            )
            return
        if parsed.path == "/settings":
            query = urllib.parse.parse_qs(parsed.query)
            self.send_html(render_settings_page(query.get("message", [""])[0], query.get("error", [""])[0]))
            return
        if parsed.path == "/api/routes":
            self.send_json([route.__dict__ for route in list_routes()])
            return
        if parsed.path == "/api/providers":
            self.send_json([provider_public(provider) for provider in list_providers()])
            return
        if parsed.path == "/api/dns":
            query = urllib.parse.parse_qs(parsed.query)
            provider_id = query.get("provider_id", [""])[0]
            domain = query.get("domain", [""])[0]
            self.send_json(netcup_records(provider_id, domain))
            return
        if parsed.path == "/api/status":
            self.send_json(collect_status())
            return
        if parsed.path == "/api/health":
            self.send_json({"ok": True, "routes_dir": str(ROUTES_DIR), "caddyfile": str(CADDYFILE_PATH)})
            return
        self.send_error(HTTPStatus.NOT_FOUND)

    def do_POST(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        form = self.read_form()
        if parsed.path == "/login":
            username = form.get("username", [""])[0]
            password = form.get("password", [""])[0]
            if secrets.compare_digest(username, USERNAME) and secrets.compare_digest(password, PASSWORD):
                token = create_session()
                self.send_response(HTTPStatus.SEE_OTHER)
                self.send_header("Location", "/")
                self.send_header(
                    "Set-Cookie",
                    f"{SESSION_COOKIE}={token}; Path=/; HttpOnly; SameSite=Lax; Max-Age={SESSION_TTL_SECONDS}",
                )
                self.end_headers()
                return
            self.redirect("/login", error="Invalid username or password.")
            return
        if not self.authorized():
            return
        if form.get("csrf", [""])[0] != CSRF_TOKEN:
            self.redirect(error="Invalid CSRF token.")
            return
        try:
            if parsed.path == "/settings":
                data = read_ui_config()
                settings = data.get("settings", {}) if isinstance(data.get("settings"), dict) else {}
                settings["domain"] = form.get("domain", [""])[0].strip().rstrip(".")
                data["settings"] = settings
                write_ui_config(data)
                self.redirect("/settings", message="Settings saved.")
                return
            if parsed.path == "/providers":
                provider = {
                    "id": form.get("id", [""])[0].strip(),
                    "type": form.get("type", ["netcup"])[0].strip().lower(),
                    "label": form.get("label", [""])[0].strip(),
                    "customer_number": form.get("customer_number", [""])[0].strip(),
                    "api_key": form.get("api_key", [""])[0].strip(),
                    "api_password": form.get("api_password", [""])[0].strip(),
                    "domains": [
                        domain.strip().rstrip(".")
                        for domain in form.get("domains", [""])[0].split(",")
                        if domain.strip()
                    ],
                }
                save_provider(provider)
                self.redirect("/settings", message=f"Provider {provider['id']} saved.")
                return
            if parsed.path == "/providers/delete":
                provider_id = form.get("provider_id", [""])[0].strip()
                delete_provider(provider_id)
                self.redirect("/settings", message=f"Provider {provider_id} deleted.")
                return
            if parsed.path in {"/dns/add", "/dns/update", "/dns/delete"}:
                provider_id = form.get("provider_id", [""])[0].strip()
                domain = form.get("domain", [""])[0].strip().rstrip(".")
                if parsed.path == "/dns/delete":
                    record = dns_record_from_form(form, delete=True)
                    action = "deleted"
                else:
                    record = dns_record_from_form(form)
                    action = "saved" if parsed.path == "/dns/update" else "added"
                netcup_update_records(provider_id, domain, [record])
                target = f"/dns?{urllib.parse.urlencode({'provider_id': provider_id, 'domain': domain})}"
                self.redirect(target, message=f"DNS record {action}.")
                return
            if parsed.path == "/logout":
                token = self.session_token()
                if token:
                    SESSIONS.pop(token, None)
                self.send_response(HTTPStatus.SEE_OTHER)
                self.send_header("Location", "/login")
                self.send_header("Set-Cookie", f"{SESSION_COOKIE}=; Path=/; HttpOnly; SameSite=Lax; Max-Age=0")
                self.end_headers()
                return
            if parsed.path == "/routes":
                basic_auth_user = form.get("basic_auth_user", [""])[0].strip()
                basic_auth_password = form.get("basic_auth_password", [""])[0]
                basic_auth_hash = hash_basic_auth_password(basic_auth_password) if basic_auth_user and basic_auth_password else ""
                route = Route(
                    name=form.get("name", [""])[0].strip(),
                    host=form.get("host", [""])[0].strip(),
                    upstream=form.get("upstream", [""])[0].strip(),
                    tls_skip_verify=form.get("tls_skip_verify", [""])[0] == "1",
                    basic_auth_user=basic_auth_user,
                    basic_auth_hash=basic_auth_hash,
                )
                change_with_rollback(lambda: save_route(route))
                self.redirect(message=f"Route {route.name} saved.")
                return
            if parsed.path == "/routes/delete":
                name = form.get("name", [""])[0].strip()
                change_with_rollback(lambda: delete_route(name))
                self.redirect(message=f"Route {name} deleted.")
                return
            if parsed.path == "/reload":
                reload_caddy()
                self.redirect(message="Caddy reloaded.")
                return
        except Exception as exc:
            self.redirect(error=str(exc))
            return
        self.send_error(HTTPStatus.NOT_FOUND)

    def read_form(self) -> dict[str, list[str]]:
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length).decode("utf-8")
        return urllib.parse.parse_qs(body)

    def session_token(self) -> str:
        cookies = parse_cookies(self.headers.get("Cookie", ""))
        return cookies.get(SESSION_COOKIE, "")

    def is_authenticated(self) -> bool:
        if not PASSWORD:
            return True
        return valid_session(self.session_token())

    def authorized(self) -> bool:
        if self.is_authenticated():
            return True
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path.startswith("/api/"):
            content = json.dumps({"error": "authentication required"}).encode("utf-8")
            self.send_response(HTTPStatus.UNAUTHORIZED)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)
            return False
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", "/login")
        self.end_headers()
        return False

    def redirect(self, path: str = "/", message: str = "", error: str = "") -> None:
        query = urllib.parse.urlencode({"message": message, "error": error})
        location = path
        if query:
            location = f"{path}&{query}" if "?" in path else f"{path}?{query}"
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", location)
        self.end_headers()

    def send_html(self, content: bytes) -> None:
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def send_json(self, data) -> None:
        content = json.dumps(data, indent=2).encode("utf-8")
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def log_message(self, fmt: str, *args) -> None:
        sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))


def main() -> int:
    ROUTES_DIR.mkdir(parents=True, exist_ok=True)
    if not PASSWORD:
        sys.stderr.write("WARNING: CADDY_UI_PASSWORD is not set. Do not expose this UI publicly.\n")
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    sys.stderr.write(f"Caddy UI listening on {HOST}:{PORT}\n")
    server.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
