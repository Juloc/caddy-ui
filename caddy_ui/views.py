from __future__ import annotations

import html
import json
import sqlite3
import urllib.parse
from dataclasses import asdict
from typing import Any, Iterable

from .domain import AccessGroup, ManagedRoute, Role, RouteKind


ICONS = {
    "menu": '<path d="M4 7h16M4 12h16M4 17h16"/>',
    "dashboard": '<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
    "routes": '<circle cx="6" cy="6" r="2"/><circle cx="18" cy="6" r="2"/><circle cx="12" cy="18" r="2"/><path d="M8 6h8M7.5 7.5l3.2 8M16.5 7.5l-3.2 8"/>',
    "access": '<circle cx="12" cy="8" r="4"/><path d="M4.5 21a7.5 7.5 0 0 1 15 0"/>',
    "logs": '<path d="M5 4h14v16H5zM8 8h8M8 12h8M8 16h5"/>',
    "system": '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2h-4V21a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H2.8v-4H3a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1a1.7 1.7 0 0 0 1.9.3A1.7 1.7 0 0 0 10 3V2.8h4V3a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.2v4H21a1.7 1.7 0 0 0-1.6 1z"/>',
    "dns": '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a15 15 0 0 1 0 18M12 3a15 15 0 0 0 0 18"/>',
    "admin": '<path d="M12 3 4 6v5c0 5 3.4 8.5 8 10 4.6-1.5 8-5 8-10V6l-8-3z"/><path d="m9 12 2 2 4-4"/>',
    "users": '<circle cx="9" cy="8" r="3"/><path d="M3.5 19a5.5 5.5 0 0 1 11 0M16 8a3 3 0 0 1 0 6M17 16a4 4 0 0 1 3.5 3"/>',
    "audit": '<path d="M5 3h14v18H5zM8 7h8M8 11h8M8 15h5"/>',
    "settings": '<circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M5 5l2 2M17 17l2 2M19 5l-2 2M7 17l-2 2"/>',
    "plus": '<path d="M12 5v14M5 12h14"/>',
    "edit": '<path d="m4 20 4-.8L19 8.2 15.8 5 4.8 16zM14.8 6l3.2 3.2"/>',
    "trash": '<path d="M4 7h16M9 7V4h6v3M6 7l1 14h10l1-14M10 11v6M14 11v6"/>',
    "refresh": '<path d="M20 7v5h-5M4 17v-5h5M18.5 9A7 7 0 0 0 6.7 6.7L4 9M5.5 15A7 7 0 0 0 17.3 17.3L20 15"/>',
    "download": '<path d="M12 3v12m0 0 5-5m-5 5-5-5M4 20h16"/>',
    "close": '<path d="m6 6 12 12M18 6 6 18"/>',
    "check": '<path d="m5 12 4 4L19 6"/>',
}


def e(value: Any) -> str:
    return html.escape(str(value if value is not None else ""), quote=True)


def icon(name: str) -> str:
    return f'<svg class="icon" viewBox="0 0 24 24" aria-hidden="true">{ICONS.get(name, ICONS["settings"])}</svg>'


def _active(current: str, name: str) -> str:
    return "active" if current == name else ""


def layout(
    title: str,
    current: str,
    session: sqlite3.Row,
    csrf: str,
    body: str,
    message: str = "",
    error: str = "",
) -> bytes:
    theme = e(session["theme"] or "system")
    accent = e(json.loads(session["accent_json"]) if session["accent_json"] else "#0f6cbd")
    users_link = f'<a class="{_active(current, "users")}" href="/admin/users">Users</a>' if session["role"] == "admin" else ""
    administration_open = " open" if current in {"users", "audit", "settings"} else ""
    notice = f'<div class="notice">{e(message)}</div>' if message else ""
    error_notice = f'<div class="notice error" role="alert">{e(error)}</div>' if error else ""
    html_value = f"""<!doctype html>
<html lang="en" style="--accent:{accent}" data-theme-preference="{theme}"{f' data-theme="{theme}"' if theme != 'system' else ''}>
<head>
  <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
  <title>{e(title)} · Caddy UI</title>
  <link rel="stylesheet" href="/static/app.css">
</head>
<body>
<div class="app">
  <aside class="sidebar" aria-label="Main navigation">
    <div class="brand"><span class="brand-mark">C</span><span>Caddy UI</span></div>
    <nav class="nav">
      <a class="{_active(current, 'dashboard')}" href="/">{icon('dashboard')}Dashboard</a>
      <a class="{_active(current, 'routes')}" href="/routes">{icon('routes')}Routes</a>
      <a class="{_active(current, 'access')}" href="/access">{icon('access')}Access</a>
      <a class="{_active(current, 'logs')}" href="/logs">{icon('logs')}Logs</a>
      <a class="{_active(current, 'system')}" href="/system">{icon('system')}System</a>
      <a class="{_active(current, 'dns')}" href="/dns">{icon('dns')}DNS</a>
    </nav>
    <div class="sidebar-bottom">
      <nav class="nav">
        <details{administration_open}>
          <summary>{icon('admin')}Administration</summary>
          <div class="subnav">
            {users_link}
            <a class="{_active(current, 'audit')}" href="/admin/audit">Audit Log</a>
            <a class="{_active(current, 'settings')}" href="/admin/settings">Settings</a>
          </div>
        </details>
      </nav>
    </div>
  </aside>
  <section class="workspace">
    <header class="topbar">
      <div class="top-actions"><button class="icon-button menu-toggle" data-menu-toggle aria-label="Open menu">{icon('menu')}</button><h1>{e(title)}</h1></div>
      <div class="top-actions"><span class="muted">{e(session['display_name'])}</span><form method="post" action="/logout"><input type="hidden" name="csrf" value="{e(csrf)}"><button type="submit">Sign out</button></form></div>
    </header>
    <main class="content">{notice}{error_notice}{body}</main>
  </section>
</div>
<script src="/static/app.js"></script>
</body></html>"""
    return html_value.encode("utf-8")


def login(error: str = "") -> bytes:
    error_html = f'<div class="notice error" role="alert">{e(error)}</div>' if error else ""
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Sign in · Caddy UI</title><link rel="stylesheet" href="/static/app.css"></head>
<body><main class="login-shell"><section class="login-card"><div class="brand-mark">C</div><h1>Sign in to Caddy UI</h1><p class="muted">Manage routes, DNS, access and system health.</p>{error_html}<form method="post" action="/login" class="stack"><label>Username<input name="username" autocomplete="username" required autofocus></label><label>Password<input name="password" type="password" autocomplete="current-password" required></label><label>TOTP code <span class="muted">(if enabled)</span><input name="totp" inputmode="numeric" autocomplete="one-time-code"></label><button class="primary" type="submit">Sign in</button></form></section></main></body></html>""".encode("utf-8")


def bars(items: list[tuple[str, int]]) -> str:
    maximum = max((count for _, count in items), default=1)
    return '<div class="bars">' + "".join(
        f'<div class="bar-row"><span class="ellipsis" title="{e(label)}">{e(label)}</span><span class="bar-track"><span class="bar" style="display:block;width:{count / maximum * 100:.1f}%"></span></span><strong>{count}</strong></div>'
        for label, count in items
    ) + "</div>"


def dashboard(
    session: sqlite3.Row,
    csrf: str,
    routes: list[ManagedRoute],
    health: dict[str, dict[str, Any]],
    caddy: dict[str, Any],
    certificates: list[dict[str, Any]],
    traffic: dict[str, Any],
    providers: list[dict[str, Any]],
    version: str,
    notifications: Iterable[sqlite3.Row],
    message: str = "",
    error: str = "",
) -> bytes:
    problems: list[str] = []
    if not caddy.get("admin"):
        problems.append("Caddy admin API is unavailable.")
    for route in routes:
        state = health.get(route.id, {})
        if route.enabled and state and not state.get("public", {}).get("ok"):
            problems.append(f"{route.effective_host} is not publicly reachable.")
        if route.enabled and state and not state.get("upstream", {}).get("ok"):
            problems.append(f"{route.name} upstream is unavailable.")
    for certificate in certificates:
        if certificate["days"] < 21:
            problems.append(f"Certificate {certificate['name']} expires in {certificate['days']} days.")
    notification_rows = "".join(f'<tr><td><span class="status {"bad" if row["severity"] == "error" else "warn"}">{e(row["severity"])}</span></td><td><strong>{e(row["title"])}</strong><div class="muted">{e(row["message"])}</div></td><td class="muted">{e(row["created_at"])}</td><td><form method="post" action="/notifications/acknowledge"><input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="notification_id" value="{e(row["id"])}"><button type="submit">Dismiss</button></form></td></tr>' for row in notifications)
    problem_rows = "".join(f'<tr><td><span class="status bad">Issue</span></td><td>{e(problem)}</td></tr>' for problem in problems)
    if not problem_rows and not notification_rows:
        problem_rows = '<tr><td colspan="3"><span class="status ok">No active problems</span></td></tr>'
    body = f"""
<div class="grid">
  <section class="panel span-12"><div class="panel-header"><h2>Problems</h2><a href="/system">Open System</a></div><div class="table-wrap"><table><tbody>{problem_rows}{notification_rows}</tbody></table></div></section>
  <section class="card span-8"><h2>Traffic by host</h2><div class="muted">Rolling 30-day aggregates · {traffic.get('requests', 0)} requests</div>{bars(traffic.get('hosts', []))}</section>
  <section class="card span-4"><h2>Status codes</h2>{bars(traffic.get('statuses', []))}</section>
  <section class="card span-4"><div class="muted">Routes</div><div class="stat-value">{len(routes)}</div></section>
  <section class="card span-4"><div class="muted">Healthy public routes</div><div class="stat-value">{sum(1 for value in health.values() if value.get('public', {}).get('ok'))}</div></section>
  <section class="card span-4"><div class="muted">Domains</div><div class="stat-value">{len({route.domain for route in routes if route.domain})}</div></section>
  <section class="card span-4"><div class="muted">Certificates</div><div class="stat-value">{len(certificates)}</div></section>
  <section class="card span-4"><div class="muted">DNS providers</div><div class="stat-value">{len(providers)}</div></section>
  <section class="card span-4"><div class="muted">Caddy admin</div><div class="stat-value {'success-text' if caddy.get('admin') else 'danger-text'}">{'Online' if caddy.get('admin') else 'Offline'}</div><div class="muted">Caddy UI {e(version)}</div></section>
</div>"""
    return layout("Dashboard", "dashboard", session, csrf, body, message, error)


def _route_form(route: ManagedRoute, csrf: str, groups: list[AccessGroup], preview_diff: str = "", proposed_json: str = "", is_admin: bool = False) -> str:
    upstreams = "\n".join(item.address for item in route.upstreams)
    paths = "\n".join(route.paths)
    request_headers = "\n".join(f"{item.operation} {item.name}: {item.value}" for item in route.request_headers)
    response_headers = "\n".join(f"{item.operation} {item.name}: {item.value}" for item in route.response_headers)
    group_options = '<option value="">No access group</option>' + "".join(f'<option value="{e(group.id)}" {"selected" if group.id == route.access_group_id else ""}>{e(group.name)}</option>' for group in groups)
    available_kinds = list(RouteKind) if is_admin else [RouteKind.PROXY, RouteKind.REDIRECT]
    kind_options = "".join(f'<option value="{kind.value}" {"selected" if route.kind == kind else ""}>{kind.value.title()}</option>' for kind in available_kinds)
    preview = ""
    if proposed_json:
        preview = f'<div class="wide"><h3>Validated preview</h3><pre class="diff">{e(preview_diff or "No textual changes.")}</pre><input type="hidden" name="preview_id" value="{e(proposed_json)}"><input type="hidden" name="route_id" value="{e(route.id)}"></div>'
    return f"""
<dialog id="route-dialog" data-auto-open aria-labelledby="route-dialog-title">
  <form method="post" action="{'/routes/apply' if proposed_json else '/routes/preview'}">
    <div class="dialog-header"><h2 id="route-dialog-title">{'Review route' if proposed_json else ('Edit route' if route.created_at else 'Create route')}</h2><button type="button" class="icon-button" data-dialog-close aria-label="Close">{icon('close')}</button></div>
    <div class="dialog-body"><div class="form-grid">
      <input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="id" value="{e(route.id)}">
      <label>Name<input name="name" value="{e(route.name)}" required></label>
      <label>Type<select name="kind">{kind_options}</select></label>
      <label>Domain<input name="domain" value="{e(route.domain)}" placeholder="example.com"></label>
      <label>Host override<input name="host" value="{e(route.host)}" placeholder="Optional"></label>
      <label class="wide">Upstreams <span class="muted">(one per line)</span><textarea name="upstreams" placeholder="app:8080">{e(upstreams)}</textarea></label>
      <label class="check wide"><input name="enabled" type="checkbox" value="1" {'checked' if route.enabled else ''}>Enabled</label>
      <details class="advanced"><summary>Advanced settings</summary><div class="advanced-grid">
        <label class="wide">Path matchers<textarea name="paths" placeholder="/api/*">{e(paths)}</textarea></label>
        <label>Load balancing<select name="load_balancing">{''.join(f'<option value="{value}" {"selected" if route.load_balancing == value else ""}>{value}</option>' for value in ['random','round_robin','least_conn','first','ip_hash'])}</select></label>
        <label>Health URI<input name="health_uri" value="{e(route.health_uri)}" placeholder="/health"></label>
        <label>Health interval<input name="health_interval" value="{e(route.health_interval)}"></label>
        <label>Access group<select name="access_group_id">{group_options}</select></label>
        <label class="check"><input name="tls_skip_verify" type="checkbox" value="1" {'checked' if route.tls_skip_verify else ''}>Skip upstream TLS verification</label>
        <label class="wide">Request headers <span class="muted">set/add/delete Header: value</span><textarea name="request_headers">{e(request_headers)}</textarea></label>
        <label class="wide">Response headers<textarea name="response_headers">{e(response_headers)}</textarea></label>
        <label>Redirect destination<input name="redirect_to" value="{e(route.redirect_to)}"></label>
        <label>Redirect status<select name="redirect_status">{''.join(f'<option value="{value}" {"selected" if route.redirect_status == value else ""}>{value}</option>' for value in [301,302,303,307,308])}</select></label>
        {f'<label class="wide">Custom Route snippet <span class="muted">(administrators only)</span><textarea name="custom_snippet">{e(route.custom_snippet)}</textarea></label>' if is_admin else ''}
      </div></details>{preview}
    </div></div>
    <div class="dialog-footer"><button type="button" data-dialog-close>Cancel</button><button class="primary" type="submit">{'Apply route' if proposed_json else 'Validate and preview'}</button></div>
  </form>
</dialog>"""


def routes_page(
    session: sqlite3.Row,
    csrf: str,
    routes: list[ManagedRoute],
    groups: list[AccessGroup],
    health: dict[str, dict[str, Any]],
    request_counts: dict[str, int],
    edit: ManagedRoute | None = None,
    preview_diff: str = "",
    proposed_json: str = "",
    message: str = "",
    error: str = "",
) -> bytes:
    rows = []
    group_names = {item.id: item.name for item in groups}
    for route in routes:
        state = health.get(route.id, {})
        public = state.get("public", {})
        upstream = state.get("upstream", {})
        rows.append(f"""<tr><td><input type="checkbox" name="route_ids" value="{e(route.id)}"></td><td data-column="state"><span class="status {'ok' if route.enabled else 'warn'}">{'Enabled' if route.enabled else 'Disabled'}</span></td><td data-column="route"><strong>{e(route.name)}</strong><div class="muted mono">{e(route.effective_host)}</div></td><td data-column="target" class="mono">{e(', '.join(item.address for item in route.upstreams) or route.redirect_to or 'Custom')}</td><td data-column="type"><span class="badge">{e(route.kind.value)}</span></td><td data-column="access">{e(group_names.get(route.access_group_id, '—'))}</td><td data-column="health"><span class="status {'ok' if public.get('ok') else 'bad'}" title="{e(public.get('detail','Not checked'))}">Public</span> <span class="status {'ok' if upstream.get('ok') else 'bad'}" title="{e(upstream.get('detail','Not checked'))}">Upstream</span></td><td data-column="requests">{request_counts.get(route.effective_host, 0)}</td><td data-column="changed" class="muted">{e(route.updated_at)}</td><td><a class="button icon-button" href="/routes?edit={e(route.id)}" aria-label="Edit route">{icon('edit')}</a></td></tr>""")
    column_choices = "".join(
        f'<label class="check"><input type="checkbox" data-route-column="{column}" checked>{label}</label>'
        for column, label in (("state", "State"), ("target", "Target"), ("type", "Type"), ("access", "Access"), ("health", "Health"), ("requests", "Requests"), ("changed", "Last change"))
    )
    body = f"""
<form method="post" action="/routes/bulk">
  <input type="hidden" name="csrf" value="{e(csrf)}">
  <div class="commandbar"><div class="commands"><a class="button primary" href="/routes?new=1">{icon('plus')}New route</a><button type="button" data-dialog-open="import-dialog">Import</button><select name="action" style="width:auto"><option value="enable">Enable</option><option value="disable">Disable</option><option value="duplicate">Duplicate</option><option value="export">Export</option><option value="delete">Delete</option></select><button type="submit">Apply to selected</button></div><div class="filters"><details class="column-picker"><summary class="button">Columns</summary><div>{column_choices}</div></details><input style="width:240px" data-filter-table="route-table" placeholder="Search routes"></div></div>
  <section class="panel"><div class="table-wrap"><table id="route-table"><thead><tr><th><input type="checkbox" data-select-all></th><th data-column="state">State</th><th data-column="route">Route</th><th data-column="target">Target</th><th data-column="type">Type</th><th data-column="access">Access</th><th data-column="health">Health</th><th data-column="requests">Requests</th><th data-column="changed">Last change</th><th></th></tr></thead><tbody>{''.join(rows) if rows else '<tr><td colspan="10" class="empty">No managed routes yet.</td></tr>'}</tbody></table></div></section>
</form>
<dialog id="import-dialog" aria-labelledby="import-title"><div class="dialog-header"><h2 id="import-title">Import routes</h2><button type="button" class="icon-button" data-dialog-close aria-label="Close">{icon('close')}</button></div><div class="dialog-body"><form method="post" action="/routes/import" class="stack"><input type="hidden" name="csrf" value="{e(csrf)}"><h3>Managed route export</h3><label>Export JSON<textarea name="import_json" rows="10" required></textarea></label><p class="muted">Existing route names are never overwritten.</p><div><button class="primary" type="submit">Import managed routes</button></div></form>{f'<form method="post" action="/routes/import-custom" class="form-grid import-custom"><input type="hidden" name="csrf" value="{e(csrf)}"><h3 class="wide">Unmanaged Caddy snippet</h3><label>Name<input name="name" required></label><label>Domain<input name="domain" placeholder="{e(routes[0].domain if routes else "example.com")}"></label><label class="wide">Host override<input name="host"></label><label class="wide">Controlled directives<textarea name="custom_snippet" rows="10" required></textarea></label><p class="muted wide">The snippet is wrapped in a managed route, previewed, validated, and never overwrites an existing route.</p><div class="wide"><button type="submit">Preview custom import</button></div></form>' if session['role'] == 'admin' else ''}</div><div class="dialog-footer"><button type="button" data-dialog-close>Close</button></div></dialog>
{_route_form(edit, csrf, groups, preview_diff, proposed_json, session['role'] == 'admin') if edit else ''}"""
    return layout("Routes", "routes", session, csrf, body, message, error)


def access_page(session: sqlite3.Row, csrf: str, groups: list[AccessGroup], credentials: dict[str, list[sqlite3.Row]], message: str = "", error: str = "") -> bytes:
    sections = []
    for group in groups:
        credential_rows = "".join(f'<tr><td>{e(row["username"])}</td><td><span class="status {"ok" if row["enabled"] else "warn"}">{"Enabled" if row["enabled"] else "Disabled"}</span></td><td><form method="post" action="/access/credentials/delete" data-confirm="Delete this credential?"><input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="credential_id" value="{e(row["id"])}"><button class="icon-button danger" aria-label="Delete">{icon('trash')}</button></form></td></tr>' for row in credentials.get(group.id, []))
        sections.append(f"""<section class="panel span-6"><div class="panel-header"><div><h2>{e(group.name)}</h2><div class="muted">{e(group.title)}</div></div><form method="post" action="/access/delete" data-confirm="Delete this access group?"><input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="group_id" value="{e(group.id)}"><button class="icon-button danger">{icon('trash')}</button></form></div><details class="inline-editor"><summary>Edit portal branding</summary><form method="post" action="/access/save" class="form-grid"><input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="group_id" value="{e(group.id)}"><label>Name<input name="name" value="{e(group.name)}" required></label><label>Portal title<input name="title" value="{e(group.title)}"></label><label class="wide">Help text<input name="help_text" value="{e(group.help_text)}"></label><label>Accent<input name="accent" type="color" value="{e(group.accent)}"></label><label class="wide">Logo URL or image data URL<input name="logo_data" value="{e(group.logo_data)}" placeholder="https://…"></label><div class="wide"><button type="submit">Save branding</button></div></form></details><div class="table-wrap"><table><thead><tr><th>Username</th><th>State</th><th></th></tr></thead><tbody>{credential_rows or '<tr><td colspan="3" class="empty">No credentials.</td></tr>'}</tbody></table></div><form method="post" action="/access/credentials/save" class="form-grid" style="padding:12px"><input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="group_id" value="{e(group.id)}"><label>Username<input name="username" required></label><label>Password<input name="password" type="password" required></label><div class="wide"><button type="submit">Add credential</button></div></form></section>""")
    body = f"""<div class="commandbar"><div class="muted">Reusable branded sign-in portals for protected routes.</div></div><div class="grid">{''.join(sections)}<section class="panel span-6"><div class="panel-header"><h2>New access group</h2></div><form method="post" action="/access/save" class="form-grid" style="padding:12px"><input type="hidden" name="csrf" value="{e(csrf)}"><label>Name<input name="name" required></label><label>Portal title<input name="title" value="Sign in"></label><label class="wide">Help text<input name="help_text"></label><label>Accent<input name="accent" type="color" value="#0f6cbd"></label><label class="wide">Logo URL or image data URL<input name="logo_data" placeholder="https://…"></label><div class="wide"><button class="primary" type="submit">Create group</button></div></form></section></div>"""
    return layout("Access", "access", session, csrf, body, message, error)


def logs_page(session: sqlite3.Row, csrf: str, tab: str, entries: list[Any], message: str = "", error: str = "") -> bytes:
    tabs = "".join(f'<a class="{"active" if tab == value else ""}" href="/logs?tab={value}">{label}</a>' for value, label in [("access", "Access"), ("system", "Caddy / System"), ("ddns", "DDNS / DNS")])
    rows = []
    if tab == "access":
        rows = [f'<div class="log-row" data-host="{e(item.get("host"))}" data-status="{e(item.get("status"))}" data-severity="{("error" if int(item.get("status", 0)) >= 500 else "warning" if int(item.get("status", 0)) >= 400 else "info")}"><span>{e(item.get("timestamp"))}</span><span>{e(item.get("status"))}</span><span>{e(item.get("host"))}</span><span>{e(item.get("method"))} {e(item.get("uri"))}</span><span>{e(item.get("duration"))}</span></div>' for item in entries]
    else:
        rows = [f'<div class="log-row" data-severity="{("error" if "error" in str(item).lower() or "failed" in str(item).lower() else "warning" if "warn" in str(item).lower() else "info")}"><span></span><span>{e(tab)}</span><span></span><span>{e(item)}</span><span></span></div>' for item in entries]
    hosts = sorted({str(item.get("host", "")) for item in entries if isinstance(item, dict) and item.get("host")})
    statuses = sorted({str(item.get("status", "")) for item in entries if isinstance(item, dict) and item.get("status")})
    body = f"""<div class="tabs">{tabs}</div><div class="commandbar"><div class="filters"><input style="width:260px" data-log-filter placeholder="Search logs"><select data-log-host style="width:170px"><option value="">All hosts</option>{''.join(f'<option value="{e(value)}">{e(value)}</option>' for value in hosts)}</select><select data-log-status style="width:120px"><option value="">All statuses</option>{''.join(f'<option value="{e(value)}">{e(value)}</option>' for value in statuses)}</select><select data-log-severity style="width:120px"><option value="">All levels</option><option value="info">Info</option><option value="warning">Warning</option><option value="error">Error</option></select><button type="button" data-live-logs>Pause live</button></div><a class="button" data-log-download href="/logs/download?tab={e(tab)}">{icon('download')}Download view</a></div><section class="panel"><div class="log-view">{''.join(rows) or '<div class="empty">No log entries.</div>'}</div></section>"""
    return layout("Logs", "logs", session, csrf, body, message, error)


def system_page(session: sqlite3.Row, csrf: str, status: dict[str, Any], certificates: list[dict[str, Any]], revisions: Iterable[sqlite3.Row], backups: list[str], message: str = "", error: str = "") -> bytes:
    cert_rows = "".join(f'<tr><td><strong>{e(item["name"])}</strong></td><td>{e(", ".join(item["names"]))}</td><td>{e(item["expires_at"])}</td><td><span class="status {"ok" if item["days"] >= 21 else "warn"}">{item["days"]} days</span></td></tr>' for item in certificates)
    revision_rows = "".join(f'<tr><td>{e(row["created_at"])}</td><td>{e(row["username"] or "system")}</td><td>{e(row["reason"])}</td><td class="mono">{e(row["digest"][:12])}</td><td><form method="post" action="/system/revisions/restore" data-confirm="Restore this configuration revision?"><input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="revision_id" value="{e(row["id"])}"><button type="submit">Restore</button></form></td></tr>' for row in revisions)
    backup_options = "".join(f'<option value="{e(item)}">{e(item)}</option>' for item in backups)
    body = f"""
<div class="commandbar"><div class="commands"><form method="post" action="/system/validate"><input type="hidden" name="csrf" value="{e(csrf)}"><button type="submit">{icon('check')}Validate config</button></form><form method="post" action="/system/reload"><input type="hidden" name="csrf" value="{e(csrf)}"><button type="submit">{icon('refresh')}Reload Caddy</button></form><a class="button" href="/system/diagnostics">{icon('download')}Diagnostics</a></div></div>
<div class="grid"><section class="card span-3"><div class="muted">Caddy admin</div><div class="stat-value {'success-text' if status.get('admin') else 'danger-text'}">{'Online' if status.get('admin') else 'Offline'}</div><div class="muted">{e(status.get('error',''))}</div></section><section class="card span-3"><div class="muted">Caddy UI version</div><div class="stat-value mono">{e(status.get('ui_version'))}</div></section><section class="card span-3"><div class="muted">Certificates</div><div class="stat-value">{len(certificates)}</div></section><section class="card span-3"><div class="muted">Configuration revisions</div><div class="stat-value">{sum(1 for _ in revisions)}</div></section>
<section class="panel span-12"><div class="panel-header"><h2>Certificates</h2></div><div class="table-wrap"><table><thead><tr><th>Name</th><th>Domains</th><th>Expires</th><th>State</th></tr></thead><tbody>{cert_rows or '<tr><td colspan="4" class="empty">No certificates found.</td></tr>'}</tbody></table></div></section>
<section class="panel span-12"><div class="panel-header"><h2>Configuration revisions</h2></div><div class="table-wrap"><table><thead><tr><th>Time</th><th>User</th><th>Reason</th><th>Digest</th><th></th></tr></thead><tbody>{revision_rows or '<tr><td colspan="5" class="empty">No revisions yet.</td></tr>'}</tbody></table></div></section>
<section class="panel span-6"><div class="panel-header"><h2>Backups</h2></div><form method="post" action="/system/backups/create" class="stack" style="padding:12px"><input type="hidden" name="csrf" value="{e(csrf)}"><button type="submit">Create backup now</button></form><form method="post" action="/system/backups/restore" class="form-grid" style="padding:12px" data-confirm="Restore this database backup?"><input type="hidden" name="csrf" value="{e(csrf)}"><label class="wide">Backup<select name="backup">{backup_options}</select></label><div><button type="submit">Restore backup</button></div></form></section></div>"""
    return layout("System", "system", session, csrf, body, message, error)


def dns_page(session: sqlite3.Row, csrf: str, providers: list[dict[str, Any]], selected: dict[str, Any] | None, domain: str, records: list[dict[str, Any]], ddns: dict[str, Any], ddns_result: dict[str, Any], message: str = "", error: str = "") -> bytes:
    provider_options = "".join(f'<option value="{e(item["id"])}" {"selected" if selected and selected["id"] == item["id"] else ""}>{e(item["label"])}</option>' for item in providers)
    record_rows = []
    for index, item in enumerate(records):
        form_id = f"dns-record-{index}"
        record_rows.append(f'''<tr><td><form id="{form_id}" method="post" action="/dns/save"></form><input form="{form_id}" type="hidden" name="csrf" value="{e(csrf)}"><input form="{form_id}" type="hidden" name="provider_id" value="{e(selected["id"] if selected else "")}"><input form="{form_id}" type="hidden" name="domain" value="{e(domain)}"><input form="{form_id}" type="hidden" name="id" value="{e(item.get("id", ""))}"><input form="{form_id}" name="hostname" value="{e(item.get("hostname"))}"></td><td><input form="{form_id}" name="type" value="{e(item.get("type"))}"></td><td><input form="{form_id}" name="priority" value="{e(item.get("priority"))}"></td><td><input form="{form_id}" class="mono" name="destination" value="{e(item.get("destination"))}"></td><td><button form="{form_id}" type="submit">Save</button> <form method="post" action="/dns/delete" data-confirm="Delete this DNS record?" style="display:inline"><input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="provider_id" value="{e(selected["id"] if selected else "")}"><input type="hidden" name="domain" value="{e(domain)}"><input type="hidden" name="record_json" value="{e(json.dumps(item))}"><button class="icon-button danger">{icon('trash')}</button></form></td></tr>''')
    ddns_provider_options = "".join(f'<option value="{e(item["id"])}" {"selected" if ddns.get("provider_id") == item["id"] else ""}>{e(item["label"])}</option>' for item in providers)
    ddns_status = "Not run yet" if not ddns_result else (f"Last run {e(ddns_result.get('at'))}: {'OK' if ddns_result.get('ok') else e(ddns_result.get('error', 'Failed'))}")
    body = f"""<div class="commandbar"><form method="get" action="/dns" class="filters"><select name="provider_id" style="width:180px">{provider_options}</select><input name="domain" value="{e(domain)}" placeholder="example.com" style="width:220px"><button type="submit">Load records</button></form></div><div class="grid"><section class="panel span-8"><div class="panel-header"><h2>DNS records</h2></div><div class="table-wrap"><table><thead><tr><th>Host</th><th>Type</th><th>Priority</th><th>Destination</th><th></th></tr></thead><tbody>{''.join(record_rows) or '<tr><td colspan="5" class="empty">No records loaded.</td></tr>'}</tbody></table></div></section><section class="panel span-4"><div class="panel-header"><h2>Add record</h2></div><form method="post" action="/dns/save" class="stack" style="padding:12px"><input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="provider_id" value="{e(selected["id"] if selected else "")}"><input type="hidden" name="domain" value="{e(domain)}"><label>Host<input name="hostname" required></label><label>Type<select name="type">{''.join(f'<option>{kind}</option>' for kind in ['A','AAAA','CNAME','MX','TXT','SRV','CAA','NS'])}</select></label><label>Priority<input name="priority"></label><label>Destination<input name="destination" required></label><button class="primary" type="submit">Add record</button></form></section><section class="panel span-6"><div class="panel-header"><div><h2>Dynamic DNS</h2><div class="muted">{ddns_status}</div></div></div><form method="post" action="/dns/ddns" class="form-grid" style="padding:12px"><input type="hidden" name="csrf" value="{e(csrf)}"><label class="check wide"><input name="enabled" type="checkbox" value="1" {'checked' if ddns.get('enabled') else ''}>Enable scheduled DDNS</label><label>Provider<select name="provider_id">{ddns_provider_options}</select></label><label>Domain<input name="domain" value="{e(ddns.get('domain', domain))}" required></label><label>Hosts<input name="hosts" value="{e(','.join(ddns.get('hosts', ['@','*'])))}"></label><label>Interval in seconds<input name="interval" type="number" min="60" max="86400" value="{e(ddns.get('interval', 300))}"></label><label class="wide">Public IPv4 service<input name="public_ip_url" type="url" value="{e(ddns.get('public_ip_url', 'https://api64.ipify.org'))}"></label><div class="wide"><button type="submit">Save DDNS</button></div></form></section></div>"""
    return layout("DNS", "dns", session, csrf, body, message, error)


def users_page(session: sqlite3.Row, csrf: str, users: Iterable[sqlite3.Row], message: str = "", error: str = "") -> bytes:
    row_values: list[str] = []
    for row in users:
        form_id = f'user-{e(row["id"])}'
        role_options = "".join(
            f'<option value="{role.value}" {"selected" if row["role"] == role.value else ""}>{role.value.title()}</option>'
            for role in Role
        )
        row_values.append(
            f'''<tr>
  <td><form id="{form_id}" method="post" action="/admin/users/save"></form>
      <input form="{form_id}" type="hidden" name="csrf" value="{e(csrf)}">
      <input form="{form_id}" type="hidden" name="user_id" value="{e(row["id"])}">
      <input form="{form_id}" name="username" value="{e(row["username"])}"></td>
  <td><input form="{form_id}" name="display_name" value="{e(row["display_name"])}"></td>
  <td><select form="{form_id}" name="role">{role_options}</select></td>
  <td><label class="check"><input form="{form_id}" name="enabled" type="checkbox" value="1" {"checked" if row["enabled"] else ""}>Enabled</label></td>
  <td><input form="{form_id}" name="password" type="password" placeholder="Keep current"></td>
  <td>{"On" if row["totp_enabled"] else "Off"}</td>
  <td><button form="{form_id}" type="submit">Save</button>
      <form method="post" action="/admin/users/delete" data-confirm="Delete this user?" style="display:inline">
        <input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="user_id" value="{e(row["id"])}">
        <button class="icon-button danger">{icon("trash")}</button>
      </form></td>
</tr>'''
        )
    rows = "".join(row_values)
    body = f"""<div class="grid"><section class="panel span-12"><div class="panel-header"><h2>Users</h2></div><div class="table-wrap"><table><thead><tr><th>Username</th><th>Display name</th><th>Role</th><th>State</th><th>New password</th><th>TOTP</th><th></th></tr></thead><tbody>{rows}</tbody></table></div></section><section class="panel span-4"><div class="panel-header"><h2>Create user</h2></div><form method="post" action="/admin/users/save" class="stack" style="padding:12px"><input type="hidden" name="csrf" value="{e(csrf)}"><label>Username<input name="username" required></label><label>Display name<input name="display_name"></label><label>Role<select name="role">{''.join(f'<option value="{role.value}">{role.value.title()}</option>' for role in Role)}</select></label><label>Password<input name="password" type="password" required></label><input type="hidden" name="enabled" value="1"><button class="primary" type="submit">Create user</button></form></section></div>"""
    return layout("Users", "users", session, csrf, body, message, error)


def audit_page(session: sqlite3.Row, csrf: str, events: Iterable[sqlite3.Row], message: str = "", error: str = "") -> bytes:
    rows = "".join(f'<tr><td>{e(row["occurred_at"])}</td><td>{e(row["actor_username"])}</td><td><strong>{e(row["action"])}</strong><div class="muted">{e(row["result"])}</div></td><td>{e(row["object_type"])}</td><td class="mono">{e(row["object_id"])}</td><td class="mono">{e(row["correlation_id"])}</td><td><details><summary>Details</summary><pre class="audit-detail">Before\n{e(row["before_json"])}\n\nAfter\n{e(row["after_json"])}</pre></details></td></tr>' for row in events)
    body = f"""<div class="commandbar"><input style="width:260px" data-filter-table="audit-table" placeholder="Search audit events"></div><section class="panel"><div class="table-wrap"><table id="audit-table"><thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Object</th><th>ID</th><th>Correlation</th><th></th></tr></thead><tbody>{rows or '<tr><td colspan="7" class="empty">No audit events.</td></tr>'}</tbody></table></div></section>"""
    return layout("Audit Log", "audit", session, csrf, body, message, error)


def settings_page(session: sqlite3.Row, csrf: str, settings: dict[str, Any], providers: list[dict[str, Any]], current_user: sqlite3.Row | None = None, message: str = "", error: str = "") -> bytes:
    provider_rows: list[str] = []
    for item in providers:
        form_id = f'provider-{e(item["id"])}'
        provider_rows.append(f'''<tr><td><form id="{form_id}" method="post" action="/admin/providers/save"></form><input form="{form_id}" type="hidden" name="csrf" value="{e(csrf)}"><input form="{form_id}" type="hidden" name="id" value="{e(item["id"])}"><input form="{form_id}" name="label" value="{e(item["label"])}"><div class="muted mono">{e(item["id"])}</div></td><td>{e(item["type"])}</td><td><input form="{form_id}" name="domains" value="{e(", ".join(item.get("domains", [])))}"></td><td><details><summary>Environment references</summary><div class="stack provider-env"><input form="{form_id}" name="customer_number" value="{e(item.get("customer_number", "NETCUP_CUSTOMER_NUMBER"))}" aria-label="Customer number environment variable"><input form="{form_id}" name="api_key" value="{e(item.get("api_key", "NETCUP_API_KEY"))}" aria-label="API key environment variable"><input form="{form_id}" name="api_password" value="{e(item.get("api_password", "NETCUP_API_PASSWORD"))}" aria-label="API password environment variable"></div></details><button form="{form_id}" type="submit">Save</button><form method="post" action="/admin/providers/delete" data-confirm="Delete this provider?" style="display:inline"><input type="hidden" name="csrf" value="{e(csrf)}"><input type="hidden" name="provider_id" value="{e(item["id"])}"><button class="icon-button danger">{icon("trash")}</button></form></td></tr>''')
    provider_rows = "".join(provider_rows)
    theme = session["theme"]
    notifications = settings.get("notifications", {}) or {}
    webhook = notifications.get("webhook", {})
    email_settings = notifications.get("email", {})
    notification_events = [("route.public.down", "Public route down"), ("route.upstream.down", "Upstream down"), ("certificate.expiring", "Certificate expiry"), ("caddy.down", "Caddy unavailable"), ("ddns.failed", "DDNS failure"), ("backup.failed", "Backup failure")]
    def event_checks(name: str, configured: dict[str, Any]) -> str:
        selected = configured.get("events", ["*"])
        return '<div class="event-grid">' + ''.join(f'<label class="check"><input type="checkbox" name="{name}" value="{e(value)}" {"checked" if "*" in selected or value in selected else ""}>{e(label)}</label>' for value, label in notification_events) + '</div>'
    if current_user and current_user["totp_enabled"]:
        totp_body = f'<p><span class="status ok">TOTP is enabled</span></p><form method="post" action="/admin/totp/disable" data-confirm="Disable TOTP?"><input type="hidden" name="csrf" value="{e(csrf)}"><button type="submit">Disable TOTP</button></form>'
    elif current_user and current_user["totp_secret"]:
        totp_body = f'<p>Enter this secret in your authenticator:</p><p class="mono">{e(current_user["totp_secret"])}</p><form method="post" action="/admin/totp/enable" class="stack"><input type="hidden" name="csrf" value="{e(csrf)}"><label>Verification code<input name="code" inputmode="numeric" required></label><button type="submit">Verify and enable</button></form>'
    else:
        totp_body = f'<p class="muted">Add a second factor to your current account.</p><form method="post" action="/admin/totp/start"><input type="hidden" name="csrf" value="{e(csrf)}"><button type="submit">Set up TOTP</button></form>'
    body = f"""<div class="grid"><section class="panel span-6"><div class="panel-header"><h2>General</h2></div><form method="post" action="/admin/settings" class="stack" style="padding:12px"><input type="hidden" name="csrf" value="{e(csrf)}"><label>Default domain<input name="default_domain" value="{e(settings.get("default_domain", ""))}"></label><label>Theme<select name="theme"><option value="system" {"selected" if theme == "system" else ""}>System</option><option value="light" {"selected" if theme == "light" else ""}>Light</option><option value="dark" {"selected" if theme == "dark" else ""}>Dark</option></select></label><label>Accent color<input name="accent" type="color" value="{e(settings.get("accent", "#0f6cbd"))}"></label><button class="primary" type="submit">Save settings</button></form></section><section class="panel span-6"><div class="panel-header"><h2>Two-factor authentication</h2></div><div style="padding:12px">{totp_body}</div></section><section class="panel span-8"><div class="panel-header"><h2>Notifications</h2></div><form method="post" action="/admin/notifications" class="form-grid" style="padding:12px"><input type="hidden" name="csrf" value="{e(csrf)}"><label class="check wide"><input name="webhook_enabled" type="checkbox" value="1" {'checked' if webhook.get('enabled') else ''}>Enable webhook</label><label class="wide">Webhook URL<input name="webhook_url" value="{e(webhook.get('url',''))}" placeholder="https://ntfy.example/topic"></label><div class="wide"><strong>Webhook events</strong>{event_checks('webhook_events', webhook)}</div><label class="check wide"><input name="email_enabled" type="checkbox" value="1" {'checked' if email_settings.get('enabled') else ''}>Enable email</label><label>SMTP host<input name="smtp_host" value="{e(email_settings.get('host',''))}"></label><label>SMTP port<input name="smtp_port" type="number" value="{e(email_settings.get('port',25))}"></label><label>From<input name="email_from" type="email" value="{e(email_settings.get('from',''))}"></label><label>Recipient<input name="email_to" type="email" value="{e(email_settings.get('to',''))}"></label><label>SMTP username<input name="smtp_username" value="{e(email_settings.get('username',''))}"></label><label>Password environment variable<input name="smtp_password_env" value="{e(email_settings.get('password_env',''))}"></label><label class="check wide"><input name="smtp_starttls" type="checkbox" value="1" {'checked' if email_settings.get('starttls') else ''}>Use STARTTLS</label><div class="wide"><strong>Email events</strong>{event_checks('email_events', email_settings)}</div><div class="wide"><button type="submit">Save notifications</button></div></form></section><section class="panel span-12"><div class="panel-header"><h2>DNS providers</h2></div><div class="table-wrap"><table><thead><tr><th>Provider</th><th>Type</th><th>Domains</th><th></th></tr></thead><tbody>{provider_rows or '<tr><td colspan="4" class="empty">No providers.</td></tr>'}</tbody></table></div></section><section class="panel span-6"><div class="panel-header"><h2>Add Netcup provider</h2></div><form method="post" action="/admin/providers/save" class="stack" style="padding:12px"><input type="hidden" name="csrf" value="{e(csrf)}"><label>ID<input name="id" placeholder="netcup-main" required></label><label>Label<input name="label" required></label><label>Domains<input name="domains" placeholder="example.com, example.net"></label><label>Customer number environment variable<input name="customer_number" value="NETCUP_CUSTOMER_NUMBER" required></label><label>API key environment variable<input name="api_key" value="NETCUP_API_KEY" required></label><label>API password environment variable<input name="api_password" value="NETCUP_API_PASSWORD" required></label><button class="primary" type="submit">Add provider</button></form></section></div>"""
    if session["role"] != "admin":
        body = f'<section class="panel"><div class="panel-header"><h2>Two-factor authentication</h2></div><div style="padding:12px">{totp_body}</div></section>'
    return layout("Settings", "settings", session, csrf, body, message, error)


def portal_login(group: AccessGroup, error: str = "", return_to: str = "/") -> bytes:
    error_html = f'<div class="notice error">{e(error)}</div>' if error else ""
    logo = f'<img class="portal-logo" src="{e(group.logo_data)}" alt="">' if group.logo_data else '<div class="brand-mark">C</div>'
    return f"""<!doctype html><html lang="en" style="--accent:{e(group.accent)}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{e(group.title)}</title><link rel="stylesheet" href="/__caddy_ui_auth/static/app.css"></head><body><main class="login-shell"><section class="login-card">{logo}<h1>{e(group.title)}</h1><p class="muted">{e(group.help_text)}</p>{error_html}<form method="post" action="/__caddy_ui_auth/login" class="stack"><input type="hidden" name="group" value="{e(group.id)}"><input type="hidden" name="return_to" value="{e(return_to)}"><label>Username<input name="username" autocomplete="username" required></label><label>Password<input name="password" type="password" autocomplete="current-password" required></label><button class="primary" type="submit">Sign in</button></form></section></main></body></html>""".encode("utf-8")
