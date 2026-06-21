#!/usr/bin/env python3
import base64
import html
import json
import os
import re
import secrets
import shutil
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
PORT = int(os.getenv("UI_PORT", "8080"))
CADDYFILE_PATH = Path(os.getenv("CADDYFILE_PATH", "/etc/caddy/Caddyfile"))
ROUTES_DIR = Path(os.getenv("CADDY_ROUTES_DIR", "/etc/caddy/routes"))
CADDY_ADMIN_URL = os.getenv("CADDY_ADMIN_URL", "http://caddy:2019").rstrip("/")
AUTO_RELOAD = os.getenv("CADDY_AUTO_RELOAD", "true").lower() in {"1", "true", "yes"}
USERNAME = os.getenv("CADDY_UI_USERNAME", "admin")
PASSWORD = os.getenv("CADDY_UI_PASSWORD", "")
CSRF_TOKEN = secrets.token_urlsafe(32)

ROUTE_NAME_RE = re.compile(r"^[A-Za-z0-9_-]{1,48}$")
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


def route_path(name: str) -> Path:
    validate_name(name)
    return ROUTES_DIR / f"{name}.caddy"


def validate_name(name: str) -> None:
    if not ROUTE_NAME_RE.match(name):
        raise ValueError("Name may only contain letters, numbers, underscores and dashes.")


def validate_route(route: Route) -> None:
    validate_name(route.name)
    if not HOST_RE.match(route.host) or ".." in route.host:
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
    lines = [
        "# managed-by caddy-ui",
        f"{META_PREFIX} {metadata}",
        f"@{route.name} host {route.host}",
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
                host=str(data["host"]),
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
      .grid {{ grid-template-columns: 1fr; }}
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


def render_home(message: str = "", error: str = "") -> bytes:
    routes = list_routes()
    if routes:
        rows = "\n".join(
            f"""<tr>
  <td><strong>{escape(route.name)}</strong></td>
  <td><code>{escape(route.host)}</code></td>
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
            for route in routes
        )
    else:
        rows = '<tr><td colspan="5" class="muted">No managed routes yet.</td></tr>'

    body = f"""
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
        <input name="name" type="text" placeholder="overseerr" required>
      </label>
      <label>Host
        <input name="host" type="text" placeholder="overseerr.example.com" required>
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
