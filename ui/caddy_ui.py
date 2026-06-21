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
import sys
import tempfile
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
CADDY_ADMIN_URL = os.getenv("CADDY_ADMIN_URL", "http://caddy:2019").rstrip("/")
AUTO_RELOAD = os.getenv("CADDY_AUTO_RELOAD", "true").lower() in {"1", "true", "yes"}
USERNAME = os.getenv("CADDY_UI_USERNAME", "admin")
PASSWORD = os.getenv("CADDY_UI_PASSWORD", "")
CSRF_TOKEN = secrets.token_urlsafe(32)

ROUTE_NAME_RE = re.compile(r"^[A-Za-z0-9_-]{1,48}$")
DNS_LABEL_RE = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")
HOST_RE = re.compile(r"^[A-Za-z0-9*_.-]{1,253}$")
UPSTREAM_RE = re.compile(r"^(https?://)?[A-Za-z0-9_.:-]+$")
META_PREFIX = "# caddy-ui-route:"


@dataclass
class Route:
    name: str
    host: str
    upstream: str
    tls_skip_verify: bool = False

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
    if not DOMAIN:
        raise ValueError("Host is required when DOMAIN is not set.")
    if not DNS_LABEL_RE.match(name):
        raise ValueError("Host is required when the route name is not a valid DNS label.")
    return f"{name}.{DOMAIN}"


def validate_route(route: Route) -> None:
    validate_name(route.name)
    host = route.effective_host
    if not HOST_RE.match(host) or ".." in host:
        raise ValueError("Host is invalid.")
    if not UPSTREAM_RE.match(route.upstream):
        raise ValueError("Upstream is invalid. Use values like app.internal:5055 or https://app.internal:9443.")


def render_route(route: Route) -> str:
    metadata = json.dumps(
        {
            "name": route.name,
            "host": route.host,
            "upstream": route.upstream,
            "tls_skip_verify": route.tls_skip_verify,
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


def collect_status() -> dict:
    routes = list_routes()
    certificates = list_certificates()
    admin_status, admin_body = request_caddy_get("/config/")
    return {
        "domain": DOMAIN,
        "caddy_admin_url": CADDY_ADMIN_URL,
        "caddy_admin_status": admin_status,
        "caddy_admin_ok": 200 <= admin_status < 300,
        "caddy_admin_error": "" if 200 <= admin_status < 300 else admin_body,
        "caddyfile_path": str(CADDYFILE_PATH),
        "routes_dir": str(ROUTES_DIR),
        "caddy_data_path": str(CADDY_DATA_PATH),
        "auto_reload": AUTO_RELOAD,
        "route_count": len(routes),
        "certificate_count": len(certificates),
        "wildcard_certificate_count": sum(1 for cert in certificates if cert.get("wildcard")),
        "certificates": certificates,
    }


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
    input[type="text"], input[type="url"] {{
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
    <form method="post" action="/reload" class="inline">
      <input type="hidden" name="csrf" value="{CSRF_TOKEN}">
      <button class="secondary" type="submit">Reload</button>
    </form>
  </header>
  {message_html}
  {error_html}
  {body}
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

    return f"""
<div class="status-grid">
  <div class="stat"><span class="muted">Caddy Admin</span><strong><span class="pill {admin_class}">{admin_text}</span></strong></div>
  <div class="stat"><span class="muted">Routes</span><strong>{escape(status["route_count"])}</strong></div>
  <div class="stat"><span class="muted">Certificates</span><strong>{escape(status["certificate_count"])}</strong></div>
  <div class="stat"><span class="muted">Wildcard Certs</span><strong>{escape(wildcard_text)}</strong></div>
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
      <tr><th>Auto Reload</th><td>{'enabled' if status["auto_reload"] else 'disabled'}</td></tr>
    </tbody>
  </table>
</section>
<section>
  <h2>Certificates</h2>
  <div class="cert-grid">{cert_rows}</div>
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
        rows = '<tr><td colspan="5" class="muted">No managed routes yet.</td></tr>'

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
      <button type="submit">Save</button>
    </form>
  </section>
</div>
"""
    return page("Caddy UI", body, message, error)


class Handler(BaseHTTPRequestHandler):
    server_version = "CaddyUI/1.0"

    def do_GET(self) -> None:
        if not self.authorized():
            return
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == "/":
            query = urllib.parse.parse_qs(parsed.query)
            self.send_html(render_home(query.get("message", [""])[0], query.get("error", [""])[0]))
            return
        if parsed.path == "/api/routes":
            self.send_json([route.__dict__ for route in list_routes()])
            return
        if parsed.path == "/api/status":
            self.send_json(collect_status())
            return
        if parsed.path == "/api/health":
            self.send_json({"ok": True, "routes_dir": str(ROUTES_DIR), "caddyfile": str(CADDYFILE_PATH)})
            return
        self.send_error(HTTPStatus.NOT_FOUND)

    def do_POST(self) -> None:
        if not self.authorized():
            return
        parsed = urllib.parse.urlparse(self.path)
        form = self.read_form()
        if form.get("csrf", [""])[0] != CSRF_TOKEN:
            self.redirect(error="Invalid CSRF token.")
            return
        try:
            if parsed.path == "/routes":
                route = Route(
                    name=form.get("name", [""])[0].strip(),
                    host=form.get("host", [""])[0].strip(),
                    upstream=form.get("upstream", [""])[0].strip(),
                    tls_skip_verify=form.get("tls_skip_verify", [""])[0] == "1",
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

    def authorized(self) -> bool:
        if not PASSWORD:
            return True
        auth = self.headers.get("Authorization", "")
        expected = "Basic " + base64.b64encode(f"{USERNAME}:{PASSWORD}".encode("utf-8")).decode("ascii")
        if secrets.compare_digest(auth, expected):
            return True
        self.send_response(HTTPStatus.UNAUTHORIZED)
        self.send_header("WWW-Authenticate", 'Basic realm="Caddy UI"')
        self.end_headers()
        return False

    def redirect(self, message: str = "", error: str = "") -> None:
        query = urllib.parse.urlencode({"message": message, "error": error})
        self.send_response(HTTPStatus.SEE_OTHER)
        self.send_header("Location", f"/?{query}")
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
