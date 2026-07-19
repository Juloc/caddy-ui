from __future__ import annotations

import json
import sqlite3
import urllib.parse
from typing import Any, Iterable

from . import views
from .analytics import AnalyticsFilters


def q(values: dict[str, Any]) -> str:
    return urllib.parse.urlencode({key: value for key, value in values.items() if value not in (None, "")})


def format_ms(value: Any) -> str:
    number = float(value or 0)
    return f"{number / 1000:.2f} s" if number >= 1000 else f"{number:.0f} ms"


def format_bytes(value: Any) -> str:
    number = float(value or 0)
    units = ("B", "KiB", "MiB", "GiB", "TiB")
    index = 0
    while number >= 1024 and index < len(units) - 1:
        number /= 1024
        index += 1
    return f"{number:.1f} {units[index]}" if index else f"{int(number)} B"


def metric(label: str, value: str, detail: str = "", href: str = "") -> str:
    inner = f'<div class="muted">{views.e(label)}</div><div class="stat-value">{views.e(value)}</div>'
    if detail:
        inner += f'<div class="muted metric-detail">{views.e(detail)}</div>'
    if href:
        return f'<a class="card metric-link span-3" href="{views.e(href)}">{inner}</a>'
    return f'<section class="card span-3">{inner}</section>'


def range_nav(path: str, selected: str, filters: AnalyticsFilters, extra: dict[str, str] | None = None) -> str:
    items = []
    base = filters.as_query()
    base.update(extra or {})
    for value in ("15m", "1h", "6h", "24h", "7d", "30d", "1y"):
        items.append(
            f'<a class="range-chip {"active" if value == selected else ""}" href="{path}?{q({**base, "range": value})}">{value}</a>'
        )
    items.append(f'<button type="button" class="range-chip {"active" if selected == "custom" else ""}" data-custom-range>Custom</button>')
    return '<div class="range-nav" aria-label="Time range">' + "".join(items) + "</div>"


def tabs(tab: str, query: dict[str, str]) -> str:
    values = (("overview", "Overview"), ("performance", "Performance"), ("traffic", "Traffic"), ("endpoints", "Endpoints"), ("clients", "Clients / IPs"))
    return '<div class="tabs">' + "".join(
        f'<a class="{"active" if tab == key else ""}" href="/analytics?{q({**query, "tab": key})}">{label}</a>' for key, label in values
    ) + "</div>"


def filter_form(filters: AnalyticsFilters, dimensions: dict[str, list[str]], range_name: str, tab: str, action: str) -> str:
    host_options = "".join(f'<option value="{views.e(value)}">' for value in dimensions.get("hosts", []))
    endpoint_options = "".join(f'<option value="{views.e(value)}">' for value in dimensions.get("endpoints", []))
    methods = ("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS")
    statuses = ("2xx", "3xx", "4xx", "5xx", "200", "301", "302", "400", "401", "403", "404", "429", "500", "502", "503")
    method_options = "".join(f'<option value="{value}" {"selected" if filters.method == value else ""}>{value}</option>' for value in methods)
    status_options = "".join(f'<option value="{value}" {"selected" if filters.status == value else ""}>{value}</option>' for value in statuses)
    client_options = "".join(f'<option value="{value}" {"selected" if filters.client_type == value else ""}>{label}</option>' for value, label in (("human", "Humans"), ("bot", "Bots"), ("internal", "Internal checks"), ("unknown", "Unknown")))
    category_options = "".join(f'<option value="{value}" {"selected" if filters.category == value else ""}>{label}</option>' for value, label in (("page", "Pages"), ("api", "API"), ("asset", "Assets"), ("websocket", "WebSocket"), ("other", "Other")))
    return f"""
<form method="get" action="{views.e(action)}" class="analytics-filter-grid" data-analytics-filters>
  <input type="hidden" name="range" value="{views.e(range_name)}"><input type="hidden" name="tab" value="{views.e(tab)}">
  <label>Host<input name="host" list="analytics-hosts" value="{views.e(filters.host)}" placeholder="All hosts"><datalist id="analytics-hosts">{host_options}</datalist></label>
  <label>Endpoint<input name="endpoint" list="analytics-endpoints" value="{views.e(filters.endpoint)}" placeholder="All endpoints"><datalist id="analytics-endpoints">{endpoint_options}</datalist></label>
  <label>Method<select name="method"><option value="">All</option>{method_options}</select></label>
  <label>Status<select name="status"><option value="">All</option>{status_options}</select></label>
  <label>Client<select name="client"><option value="">All</option>{client_options}</select></label>
  <label>Category<select name="category"><option value="">All</option>{category_options}</select></label>
  <label>Client IP<input name="ip" value="{views.e(filters.remote_ip)}" placeholder="Any IP"></label>
  <label>Search<input name="q" value="{views.e(filters.search)}" placeholder="Path or user-agent"></label>
  <label>Min response ms<input name="min_ms" type="number" min="0" step="1" value="{views.e('' if filters.min_duration_ms is None else f'{filters.min_duration_ms:g}')}"></label>
  <label>Max response ms<input name="max_ms" type="number" min="0" step="1" value="{views.e('' if filters.max_duration_ms is None else f'{filters.max_duration_ms:g}')}"></label>
  <div class="filter-actions"><button class="primary" type="submit">Apply filters</button><a class="button" href="{views.e(action)}">Reset</a></div>
</form>
"""


def chart(title: str, series: list[dict[str, Any]], metric_name: str, unit: str, drill_query: dict[str, str]) -> str:
    payload = json.dumps(series, separators=(",", ":"))
    return f"""
<section class="chart-section span-6"><div class="panel-header"><div><h2>{views.e(title)}</h2><div class="muted">Select a point to open matching logs</div></div></div>
<div class="analytics-chart" data-chart-series="{views.e(payload)}" data-chart-metric="{views.e(metric_name)}" data-chart-unit="{views.e(unit)}" data-chart-drill="{views.e('/logs?' + q(drill_query))}"><div class="empty">No chart data in this range.</div></div></section>
"""


def _ranked_rows(items: list[tuple[str, int, float]], dimension: str, base: dict[str, str], range_name: str, client: bool = False) -> str:
    rows = []
    for label, count, average in items:
        target = f"/analytics/client?{q({'ip': label, 'range': range_name})}" if client else f"/logs?{q({**base, dimension: label})}"
        rows.append(f'<tr><td><a href="{views.e(target)}">{views.e(label)}</a></td><td>{count:,}</td><td>{views.e(format_ms(average))}</td></tr>')
    return "".join(rows) or '<tr><td colspan="3" class="empty">No data.</td></tr>'


def analytics_page(session: sqlite3.Row, csrf: str, tab: str, range_name: str, filters: AnalyticsFilters, summary: dict[str, Any], series: list[dict[str, Any]], top_hosts: list[tuple[str, int, float]], top_endpoints: list[tuple[str, int, float]], slow_endpoints: list[tuple[str, int, float]], top_clients: list[tuple[str, int, float]], dimensions: dict[str, list[str]], saved: Iterable[sqlite3.Row], message: str = "", error: str = "") -> bytes:
    base = {**filters.as_query(), "range": range_name}
    requests = int(summary.get("requests", 0) or 0)
    errors = int(summary.get("errors_4xx", 0) or 0) + int(summary.get("errors_5xx", 0) or 0)
    error_rate = errors / requests * 100 if requests else 0
    cards = "".join((
        metric("Requests", f"{requests:,}", "Selected range", "/logs?" + q(base)),
        metric("Average", format_ms(summary.get("avg_ms")), "Response time", "/logs?" + q({**base, "sort": "slow"})),
        metric("P95", format_ms(summary.get("p95_ms")), f"P99 {format_ms(summary.get('p99_ms'))}"),
        metric("4xx / 5xx", f"{int(summary.get('errors_4xx',0)):,} / {int(summary.get('errors_5xx',0)):,}", f"{error_rate:.1f}% errors", "/logs?" + q({**base, "errors": "1"})),
        metric("Traffic", format_bytes(summary.get("bytes_sent")), "Response bytes"),
        metric("P50", format_ms(summary.get("p50_ms")), "Median response time"),
        metric("Maximum", format_ms(summary.get("max_ms")), "Slowest request"),
    ))
    if tab == "performance":
        content = f'<div class="grid analytics-grid">{chart("Response time", series, "avg_ms", "ms", base)}{chart("Errors", series, "errors", "requests", {**base, "errors":"1"})}<section class="panel span-12"><div class="panel-header"><h2>Slowest endpoints</h2></div><div class="table-wrap"><table><thead><tr><th>Endpoint</th><th>Requests</th><th>Average</th></tr></thead><tbody>{_ranked_rows(slow_endpoints,"endpoint",base,range_name)}</tbody></table></div></section></div>'
    elif tab == "traffic":
        content = f'<div class="grid analytics-grid">{chart("Requests over time", series, "requests", "requests", base)}{chart("Errors over time", series, "errors", "requests", {**base,"errors":"1"})}<section class="panel span-6"><div class="panel-header"><h2>Top hosts</h2></div><div class="table-wrap"><table><thead><tr><th>Host</th><th>Requests</th><th>Average</th></tr></thead><tbody>{_ranked_rows(top_hosts,"host",base,range_name)}</tbody></table></div></section><section class="panel span-6"><div class="panel-header"><h2>Top clients</h2></div><div class="table-wrap"><table><thead><tr><th>Client IP</th><th>Requests</th><th>Average</th></tr></thead><tbody>{_ranked_rows(top_clients,"ip",base,range_name,True)}</tbody></table></div></section></div>'
    elif tab == "endpoints":
        content = f'<div class="grid analytics-grid"><section class="panel span-6"><div class="panel-header"><div><h2>Top endpoints</h2><div class="muted">Dynamic identifiers are normalized for analytics.</div></div></div><div class="table-wrap"><table><thead><tr><th>Endpoint</th><th>Requests</th><th>Average</th></tr></thead><tbody>{_ranked_rows(top_endpoints,"endpoint",base,range_name)}</tbody></table></div></section><section class="panel span-6"><div class="panel-header"><div><h2>Slowest endpoints</h2><div class="muted">Static assets are excluded.</div></div></div><div class="table-wrap"><table><thead><tr><th>Endpoint</th><th>Requests</th><th>Average</th></tr></thead><tbody>{_ranked_rows(slow_endpoints,"endpoint",base,range_name)}</tbody></table></div></section></div>'
    elif tab == "clients":
        content = f'<section class="panel"><div class="panel-header"><div><h2>Clients / IPs</h2><div class="muted">Full IP detail is available inside the raw retention window.</div></div></div><div class="table-wrap"><table><thead><tr><th>Client IP</th><th>Requests</th><th>Average</th></tr></thead><tbody>{_ranked_rows(top_clients,"ip",base,range_name,True)}</tbody></table></div></section>'
    else:
        content = f'<div class="grid analytics-grid">{chart("Requests over time", series, "requests", "requests", base)}{chart("Average response time", series, "avg_ms", "ms", base)}<section class="panel span-6"><div class="panel-header"><h2>Top hosts</h2></div><div class="table-wrap"><table><thead><tr><th>Host</th><th>Requests</th><th>Average</th></tr></thead><tbody>{_ranked_rows(top_hosts,"host",base,range_name)}</tbody></table></div></section><section class="panel span-6"><div class="panel-header"><h2>Slow endpoints</h2></div><div class="table-wrap"><table><thead><tr><th>Endpoint</th><th>Requests</th><th>Average</th></tr></thead><tbody>{_ranked_rows(slow_endpoints,"endpoint",base,range_name)}</tbody></table></div></section></div>'
    saved_options = "".join(f'<option value="{views.e(row["query_json"])}">{views.e(row["name"])}</option>' for row in saved)
    body = f"""
<div class="commandbar analytics-commandbar">{range_nav('/analytics', range_name, filters, {'tab':tab})}<div class="commands"><a class="button" href="/logs?{q(base)}">Open logs</a></div></div>
{tabs(tab, base)}
<details class="filter-panel" {'open' if filters.as_query() else ''}><summary>Filters</summary>{filter_form(filters, dimensions, range_name, tab, '/analytics')}</details>
<div class="saved-view-bar"><label>Saved view<select data-saved-view><option value="">Choose view</option>{saved_options}</select></label><form method="post" action="/saved-views/save" class="inline-form"><input type="hidden" name="csrf" value="{views.e(csrf)}"><input type="hidden" name="kind" value="analytics"><input type="hidden" name="query" value="{views.e(json.dumps(base,separators=(',',':')))}"><input name="name" placeholder="View name" required maxlength="80"><button type="submit">Save view</button></form></div>
<div class="grid metrics-grid">{cards}</div>{content}
<div class="custom-range-popover" data-custom-range-panel hidden><form method="get" action="/analytics" class="form-grid"><input type="hidden" name="range" value="custom"><input type="hidden" name="tab" value="{views.e(tab)}"><label>Start<input type="datetime-local" name="start" required></label><label>End<input type="datetime-local" name="end"></label><button class="primary" type="submit">Apply custom range</button></form></div>
"""
    return views.layout("Analytics", "analytics", session, csrf, body, message, error)


def filter_chips(filters: AnalyticsFilters, range_name: str) -> str:
    values = {"range": range_name, **filters.as_query()}
    chips = []
    for key, value in values.items():
        if not value or (key == "range" and value == "24h"):
            continue
        reduced = dict(values); reduced.pop(key, None)
        chips.append(f'<a class="filter-chip" href="/logs?{q(reduced)}"><span>{views.e(key)}: {views.e(value)}</span><span aria-hidden="true">×</span></a>')
    return '<div class="filter-chips">' + "".join(chips) + "</div>" if chips else ""


def logs_page(session: sqlite3.Row, csrf: str, rows: Iterable[sqlite3.Row], filters: AnalyticsFilters, range_name: str, dimensions: dict[str, list[str]], total: int, page: int, saved: Iterable[sqlite3.Row], is_admin: bool, message: str = "", error: str = "") -> bytes:
    base = {**filters.as_query(), "range": range_name}
    quick = (("5xx", {**base,"status":"5xx"}), ("4xx", {**base,"status":"4xx"}), ("> 1 s", {**base,"min_ms":"1000"}), ("> 3 s", {**base,"min_ms":"3000"}), ("Errors", {**base,"errors":"1"}), ("Last 15m", {**base,"range":"15m"}), ("Bots", {**base,"client":"bot"}))
    quick_html = '<div class="quick-filters">' + "".join(f'<a class="button" href="/logs?{q(query)}">{views.e(label)}</a>' for label,query in quick) + '</div>'
    row_html = []
    for row in rows:
        status = int(row["status"] or 0); state = "bad" if status >= 500 else "warn" if status >= 400 else "ok"
        duration = float(row["duration_ms"] or 0); speed = "critical" if duration > 3000 else "slow" if duration > 1000 else "warn" if duration >= 500 else "normal"
        ip_target = f'/analytics/client?{q({"ip":row["remote_ip"],"range":range_name})}' if row["remote_ip"] else ""
        ip_cell = f'<a href="{views.e(ip_target)}">{views.e(row["remote_ip"])}</a>' if ip_target else "—"
        row_html.append(f'<tr class="request-row"><td class="nowrap">{views.e(row["occurred_at"])}</td><td>{views.e(row["host"])}</td><td><span class="method-badge">{views.e(row["method"])}</span></td><td class="request-path"><a href="/logs?{q({**base,"endpoint":row["endpoint"]})}" title="{views.e(row["uri"])}">{views.e(row["uri"])}</a><div class="muted">{views.e(row["endpoint"])}</div></td><td><span class="status {state}">{status}</span></td><td><span class="latency {speed}">{views.e(format_ms(duration))}</span></td><td>{views.e(format_bytes(row["bytes_sent"]))}</td><td>{ip_cell}</td><td><span class="badge">{views.e(row["client_type"])}</span><div class="muted">{views.e(row["category"])}</div></td><td class="ua-cell" title="{views.e(row["user_agent"])}">{views.e(row["user_agent"] or "—")}</td></tr>')
    if not row_html:
        row_html.append('<tr><td colspan="10" class="empty">No requests match these filters.</td></tr>')
    saved_options = "".join(f'<option value="{views.e(row["query_json"])}">{views.e(row["name"])}</option>' for row in saved)
    export = f'<a class="button" href="/logs/export?{q({**base,"format":"csv"})}">CSV</a><a class="button" href="/logs/export?{q({**base,"format":"json"})}">JSON</a>' if is_admin else ""
    pages = max(1, (total + 199) // 200)
    pagination = f'<div class="pagination"><span>{total:,} matching requests · Page {page} of {pages}</span>'
    if page > 1: pagination += f'<a class="button" href="/logs?{q({**base,"page":page-1})}">Previous</a>'
    if page < pages: pagination += f'<a class="button" href="/logs?{q({**base,"page":page+1})}">Next</a>'
    pagination += '</div>'
    body = f"""
<div class="commandbar">{range_nav('/logs', range_name, filters)}<div class="commands"><button type="button" data-live-requests>Live</button>{export}<a class="button" href="/logs?tab=system">Caddy/System logs</a></div></div>
{quick_html}{filter_chips(filters,range_name)}
<details class="filter-panel" open><summary>Request filters</summary>{filter_form(filters, dimensions, range_name, '', '/logs')}</details>
<div class="saved-view-bar"><label>Saved view<select data-saved-view><option value="">Choose view</option>{saved_options}</select></label><form method="post" action="/saved-views/save" class="inline-form"><input type="hidden" name="csrf" value="{views.e(csrf)}"><input type="hidden" name="kind" value="logs"><input type="hidden" name="query" value="{views.e(json.dumps(base,separators=(',',':')))}"><input name="name" placeholder="View name" required maxlength="80"><button type="submit">Save view</button></form></div>
<section class="panel request-log-panel"><div class="panel-header"><div><h2>Access requests</h2><div class="muted">Sensitive query values are redacted before persistence. Request and response bodies, cookies, and authorization headers are not stored.</div></div></div><div class="table-wrap"><table class="request-table"><thead><tr><th>Time</th><th>Host</th><th>Method</th><th>Path / endpoint</th><th>Status</th><th>Response</th><th>Size</th><th>Client IP</th><th>Type</th><th>User-Agent</th></tr></thead><tbody data-live-request-body>{''.join(row_html)}</tbody></table></div>{pagination}</section><div class="live-status" data-live-status hidden aria-live="polite"></div>
"""
    return views.layout("Logs", "logs", session, csrf, body, message, error)


def client_page(session: sqlite3.Row, csrf: str, detail: dict[str, Any], range_name: str, security_events: Iterable[sqlite3.Row], banned: bool, is_admin: bool, message: str = "", error: str = "") -> bytes:
    summary = detail["summary"]; ip = detail["ip"]
    security_rows = "".join(f'<tr><td>{views.e(row["occurred_at"])}</td><td><span class="badge">{views.e(row["kind"])}</span></td><td>{views.e(row["reason"])}</td></tr>' for row in security_events) or '<tr><td colspan="3" class="empty">No security events for this client.</td></tr>'
    requests = "".join(f'<tr><td>{views.e(row["occurred_at"])}</td><td>{views.e(row["host"])}</td><td>{views.e(row["method"])}</td><td>{views.e(row["uri"])}</td><td>{views.e(row["status"])}</td><td>{views.e(format_ms(row["duration_ms"]))}</td></tr>' for row in detail["events"]) or '<tr><td colspan="6" class="empty">No requests in this range.</td></tr>'
    action = ""
    if is_admin:
        if banned:
            action = f'<form method="post" action="/security/unban"><input type="hidden" name="csrf" value="{views.e(csrf)}"><input type="hidden" name="ip" value="{views.e(ip)}"><button type="submit">Remove temporary block</button></form>'
        else:
            action = f'<form method="post" action="/security/ban" class="inline-form"><input type="hidden" name="csrf" value="{views.e(csrf)}"><input type="hidden" name="ip" value="{views.e(ip)}"><input type="hidden" name="duration" value="86400"><input name="reason" value="Manual administrator block" required><button class="danger" type="submit">Temporarily block</button></form>'
    endpoint_rows = "".join(f'<tr><td><a href="/logs?{q({"ip":ip,"endpoint":item[0],"range":range_name})}">{views.e(item[0])}</a></td><td>{item[1]:,}</td><td>{views.e(format_ms(item[2]))}</td></tr>' for item in detail["endpoints"])
    body = f'<div class="commandbar"><div><a href="/analytics?tab=clients">← Clients</a><h2 class="page-inline-title">{views.e(ip)}</h2></div><div class="commands"><a class="button" href="/logs?{q({"ip":ip,"range":range_name})}">Open filtered logs</a>{action}</div></div><div class="grid metrics-grid">{metric("Requests",f"{int(summary.get('requests',0)):,}")}{metric("Average",format_ms(summary.get("avg_ms")))}{metric("P95",format_ms(summary.get("p95_ms")))}{metric("5xx",f"{int(summary.get('errors_5xx',0)):,}")}</div><div class="grid analytics-grid">{chart("Request activity",detail["series"],"requests","requests",{"ip":ip,"range":range_name})}<section class="panel span-6"><div class="panel-header"><h2>Top endpoints</h2></div><div class="table-wrap"><table><thead><tr><th>Endpoint</th><th>Requests</th><th>Average</th></tr></thead><tbody>{endpoint_rows or "<tr><td colspan=\"3\" class=\"empty\">No data.</td></tr>"}</tbody></table></div></section><section class="panel span-12"><div class="panel-header"><h2>Security history</h2></div><div class="table-wrap"><table><thead><tr><th>Time</th><th>Type</th><th>Reason</th></tr></thead><tbody>{security_rows}</tbody></table></div></section><section class="panel span-12"><div class="panel-header"><h2>Recent requests</h2></div><div class="table-wrap"><table><thead><tr><th>Time</th><th>Host</th><th>Method</th><th>Path</th><th>Status</th><th>Response</th></tr></thead><tbody>{requests}</tbody></table></div></section></div>'
    return views.layout(f"Client {ip}", "analytics", session, csrf, body, message, error)
