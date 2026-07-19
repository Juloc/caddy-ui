from __future__ import annotations

import sqlite3
import urllib.parse
from typing import Any, Iterable

from . import views
from .domain import ManagedRoute


def _q(values: dict[str, Any]) -> str:
    return urllib.parse.urlencode({key: value for key, value in values.items() if value not in (None, "")})


def _metric(label: str, value: str, detail: str = "") -> str:
    extra = f'<div class="muted metric-detail">{views.e(detail)}</div>' if detail else ""
    return f'<section class="card span-3"><div class="muted">{views.e(label)}</div><div class="stat-value">{views.e(value)}</div>{extra}</section>'


def security_page(
    session: sqlite3.Row,
    csrf: str,
    tab: str,
    summary: dict[str, Any],
    events: Iterable[sqlite3.Row],
    bans: Iterable[sqlite3.Row],
    settings: dict[str, Any],
    routes: list[ManagedRoute],
    is_admin: bool,
    message: str = "",
    error: str = "",
) -> bytes:
    tabs = '<div class="tabs">' + "".join(
        f'<a class="{"active" if tab == key else ""}" href="/security?tab={key}">{label}</a>'
        for key, label in (("overview", "Overview"), ("threats", "Threats"), ("blocked", "Blocked IPs"), ("limits", "Rate Limits"), ("login", "Login Protection"))
    ) + "</div>"
    event_rows = "".join(
        f'<tr><td>{views.e(row["occurred_at"])}</td><td><span class="badge">{views.e(row["severity"])}</span></td><td>{views.e(row["kind"])}</td><td><a href="/analytics/client?{_q({"ip":row["client_ip"],"range":"30d"})}">{views.e(row["client_ip"] or "—")}</a></td><td>{views.e(row["host"])}</td><td>{views.e(row["endpoint"])}</td><td>{views.e(row["reason"])}</td></tr>'
        for row in events
    ) or '<tr><td colspan="7" class="empty">No security events.</td></tr>'
    ban_rows = []
    for row in bans:
        action = ""
        if is_admin:
            action = f'<form method="post" action="/security/unban"><input type="hidden" name="csrf" value="{views.e(csrf)}"><input type="hidden" name="ip" value="{views.e(row["ip"])}"><button type="submit">Remove</button></form>'
        ban_rows.append(f'<tr><td><a href="/analytics/client?{_q({"ip":row["ip"],"range":"30d"})}">{views.e(row["ip"])}</a></td><td>{views.e(row["source"])}</td><td>{views.e(row["reason"])}</td><td>{views.e(row["expires_at"])}</td><td>{action}</td></tr>')
    ban_html = "".join(ban_rows) or '<tr><td colspan="5" class="empty">No active temporary blocks.</td></tr>'
    admin_policy = ""
    if is_admin:
        global_settings = settings["global"]
        login = settings["login"]
        level_options = "".join(f'<option value="{level}" {"selected" if settings["level"] == level else ""}>{level.title()}</option>' for level in ("off", "balanced", "strict", "custom"))
        admin_policy = f"""
<section class="panel span-12"><div class="panel-header"><div><h2>Protection policy</h2><div class="muted">Changes are validated against the full Caddy configuration and restored automatically when validation or reload fails.</div></div></div>
<form method="post" action="/security/settings" class="form-grid security-settings-form"><input type="hidden" name="csrf" value="{views.e(csrf)}">
<label>Protection level<select name="level">{level_options}</select></label><label>Requests per window<input name="requests" type="number" min="1" value="{views.e(global_settings['requests'])}"></label>
<label>Window seconds<input name="window_seconds" type="number" min="1" value="{views.e(global_settings['window_seconds'])}"></label><label>Burst<input name="burst" type="number" min="0" value="{views.e(global_settings['burst'])}"></label>
<label>Temporary restriction seconds<input name="block_seconds" type="number" min="60" max="86400" value="{views.e(global_settings['block_seconds'])}"></label><label>Login delay after failures<input name="login_delay_after" type="number" min="1" value="{views.e(login['delay_after'])}"></label>
<label>Login restriction after failures<input name="login_block_after" type="number" min="2" value="{views.e(login['block_after'])}"></label><label class="wide">Trusted proxy networks<textarea name="trusted_proxies" placeholder="10.0.0.0/8&#10;172.16.0.0/12">{views.e(chr(10).join(settings['trusted_proxies']))}</textarea></label>
<label class="wide">Allowlist networks<textarea name="allowlist" placeholder="192.168.0.0/16">{views.e(chr(10).join(settings['allowlist']))}</textarea></label><div class="wide"><button class="primary" type="submit">Apply protection</button></div></form></section>
"""
    manual_form = ""
    if is_admin:
        manual_form = f'<form method="post" action="/security/ban" class="inline-form"><input type="hidden" name="csrf" value="{views.e(csrf)}"><input name="ip" placeholder="IP address" required><select name="duration"><option value="900">15 minutes</option><option value="3600">1 hour</option><option value="86400" selected>24 hours</option><option value="604800">7 days</option></select><input name="reason" placeholder="Reason" required><button class="danger" type="submit">Add temporary block</button></form>'
    if tab == "blocked":
        content = f'<section class="panel"><div class="panel-header"><div><h2>Blocked IPs</h2><div class="muted">Automatic restrictions are temporary and never permanent.</div></div>{manual_form}</div><div class="table-wrap"><table><thead><tr><th>IP</th><th>Source</th><th>Reason</th><th>Expires</th><th></th></tr></thead><tbody>{ban_html}</tbody></table></div></section>'
    elif tab == "limits":
        route_rows = []
        overrides = settings.get("route_overrides", {}) if isinstance(settings.get("route_overrides"), dict) else {}
        for route in routes:
            override = overrides.get(route.id, {}) if isinstance(overrides, dict) else {}
            mode = str(override.get("mode", "inherit"))
            controls = views.e(mode)
            if is_admin:
                controls = f'<form method="post" action="/security/route-limit" class="route-limit-form"><input type="hidden" name="csrf" value="{views.e(csrf)}"><input type="hidden" name="route_id" value="{views.e(route.id)}"><select name="mode"><option value="inherit" {"selected" if mode == "inherit" else ""}>Inherit</option><option value="off" {"selected" if mode == "off" else ""}>Off</option><option value="custom" {"selected" if mode == "custom" else ""}>Custom</option></select><input name="requests" type="number" min="1" value="{views.e(override.get("requests",settings["global"]["requests"]))}" aria-label="Requests"><input name="window_seconds" type="number" min="1" value="{views.e(override.get("window_seconds",settings["global"]["window_seconds"]))}" aria-label="Window seconds"><input name="burst" type="number" min="0" value="{views.e(override.get("burst",settings["global"]["burst"]))}" aria-label="Burst"><button type="submit">Save</button></form>'
            route_rows.append(f'<tr><td>{views.e(route.name)}</td><td>{views.e(route.effective_host)}</td><td>{controls}</td></tr>')
        rows = "".join(route_rows) or '<tr><td colspan="3" class="empty">No routes.</td></tr>'
        content = f'<div class="grid">{admin_policy}<section class="panel span-12"><div class="panel-header"><h2>Per-route limits</h2></div><div class="table-wrap"><table><thead><tr><th>Route</th><th>Host</th><th>Policy / requests / window / burst</th></tr></thead><tbody>{rows}</tbody></table></div></section></div>'
    elif tab == "login":
        content = f'<div class="grid">{admin_policy}<section class="panel span-12"><div class="panel-header"><h2>Login protection</h2></div><div class="security-explainer"><p>Failed sign-ins are tracked by securely resolved client IP and username. Progressive delay starts after {settings["login"]["delay_after"]} failures. Temporary restrictions start after {settings["login"]["block_after"]} failures and escalate from 15 minutes to one hour and up to 24 hours for repeated attacks.</p><p>Successful sign-in clears the active failure counter. The system never creates automatic permanent account or IP bans.</p></div></section></div>'
    elif tab == "threats":
        content = f'<section class="panel"><div class="panel-header"><h2>Threats</h2></div><div class="table-wrap"><table><thead><tr><th>Time</th><th>Severity</th><th>Type</th><th>Client</th><th>Host</th><th>Endpoint</th><th>Reason</th></tr></thead><tbody>{event_rows}</tbody></table></div></section>'
    else:
        bars = views.bars([(str(ip), int(count)) for ip, count in summary.get("top_ips", [])])
        content = f'<div class="grid metrics-grid">{_metric("Security events",f"{int(summary.get('events',0)):,}","Last 24 hours")}{_metric("Blocked events",f"{int(summary.get('blocked',0)):,}",f"{int(summary.get('active_bans',0))} active")}{_metric("Brute-force events",f"{int(summary.get('brute_force',0)):,}","Last 24 hours")}{_metric("Observed clients",f"{int(summary.get('clients',0)):,}","Security events")}</div><div class="grid analytics-grid"><section class="panel span-8"><div class="panel-header"><h2>Recent threats</h2><a href="/security?tab=threats">View all</a></div><div class="table-wrap"><table><thead><tr><th>Time</th><th>Severity</th><th>Type</th><th>Client</th><th>Host</th><th>Endpoint</th><th>Reason</th></tr></thead><tbody>{event_rows}</tbody></table></div></section><section class="panel span-4"><div class="panel-header"><h2>Top attacking IPs</h2></div>{bars}</section></div>'
    body = f'<div class="commandbar"><div><span class="status {"ok" if settings["level"] != "off" else "warn"}">Protection {views.e(settings["level"].title())}</span></div><div class="commands"><a class="button" href="/logs?status=4xx&range=24h">Open related logs</a></div></div>{tabs}{content}'
    return views.layout("Security", "security", session, csrf, body, message, error)


def settings_extension(csrf: str, analytics: dict[str, Any], notifications: dict[str, Any], is_admin: bool) -> str:
    if not is_admin:
        return ""
    performance = analytics["performance"]
    discord = notifications.get("discord", {}) if isinstance(notifications, dict) else {}
    telegram = notifications.get("telegram", {}) if isinstance(notifications, dict) else {}
    return f"""
<div class="grid settings-extension">
<section class="panel span-6"><div class="panel-header"><h2>Analytics retention & performance</h2></div><form method="post" action="/analytics/settings" class="stack settings-pad"><input type="hidden" name="csrf" value="{views.e(csrf)}"><label>Full raw request retention (days)<input type="number" name="raw_retention_days" min="30" max="365" value="{views.e(analytics['raw_retention_days'])}"></label><label>Aggregate retention (days)<input type="number" name="aggregate_retention_days" min="30" max="3650" value="{views.e(analytics['aggregate_retention_days'])}"></label><label>Normal threshold (ms)<input type="number" name="normal_ms" min="1" value="{views.e(performance['normal_ms'])}"></label><label>Warning threshold (ms)<input type="number" name="warning_ms" min="1" value="{views.e(performance['warning_ms'])}"></label><label>Slow threshold (ms)<input type="number" name="slow_ms" min="1" value="{views.e(performance['slow_ms'])}"></label><label>Additional sensitive query parameter names<input name="redacted_query_names" value="{views.e(', '.join(analytics['redacted_query_names']))}" placeholder="credential, api_token"></label><button class="primary" type="submit">Save analytics settings</button></form></section>
<section class="panel span-6"><div class="panel-header"><h2>Additional alert channels</h2></div><form method="post" action="/alerts/settings" class="stack settings-pad"><input type="hidden" name="csrf" value="{views.e(csrf)}"><label class="check"><input name="discord_enabled" type="checkbox" value="1" {'checked' if discord.get('enabled') else ''}>Enable Discord</label><label>Discord webhook environment variable<input name="discord_webhook_env" value="{views.e(discord.get('webhook_env',''))}" placeholder="CADDY_UI_DISCORD_WEBHOOK"></label><label>Discord events<input name="discord_events" value="{views.e(','.join(discord.get('events',['security.threat'])))}"></label><label class="check"><input name="telegram_enabled" type="checkbox" value="1" {'checked' if telegram.get('enabled') else ''}>Enable Telegram</label><label>Telegram bot token environment variable<input name="telegram_token_env" value="{views.e(telegram.get('token_env',''))}" placeholder="CADDY_UI_TELEGRAM_TOKEN"></label><label>Telegram chat ID<input name="telegram_chat_id" value="{views.e(telegram.get('chat_id',''))}"></label><label>Telegram events<input name="telegram_events" value="{views.e(','.join(telegram.get('events',['security.threat'])))}"></label><p class="muted">Tokens and Discord webhook URLs are read from environment variables and are never persisted in SQLite.</p><button class="primary" type="submit">Save alert channels</button></form></section>
</div>
"""
