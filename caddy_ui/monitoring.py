from __future__ import annotations

import datetime as dt
import json
import socket
import ssl
import urllib.parse
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any

from .config import Settings
from .domain import ManagedRoute, RouteKind


def tail_lines(path: Path, max_bytes: int = 1024 * 1024, limit: int = 1000) -> list[str]:
    try:
        size = path.stat().st_size
        with path.open("rb") as handle:
            handle.seek(max(0, size - max_bytes))
            data = handle.read().decode("utf-8", errors="replace")
        lines = data.splitlines()
        if size > max_bytes and lines:
            lines = lines[1:]
        return lines[-limit:]
    except OSError:
        return []


def parse_access_logs(path: Path, limit: int = 500) -> list[dict[str, Any]]:
    values: list[dict[str, Any]] = []
    for line in tail_lines(path, limit=limit):
        try:
            item = json.loads(line)
        except json.JSONDecodeError:
            continue
        request = item.get("request", {}) if isinstance(item.get("request"), dict) else {}
        uri = str(request.get("uri", ""))
        parsed_uri = urllib.parse.urlsplit(uri)
        query = urllib.parse.parse_qsl(parsed_uri.query, keep_blank_values=True)
        safe_query = urllib.parse.urlencode((key, "[redacted]" if any(word in key.lower() for word in ("token", "secret", "password", "key", "code")) else item) for key, item in query)
        safe_uri = urllib.parse.urlunsplit((parsed_uri.scheme, parsed_uri.netloc, parsed_uri.path, safe_query, parsed_uri.fragment))
        values.append(
            {
                "timestamp": item.get("ts", ""),
                "host": request.get("host", ""),
                "method": request.get("method", ""),
                "uri": safe_uri,
                "status": int(item.get("status", 0) or 0),
                "size": int(item.get("size", 0) or 0),
                "duration": float(item.get("duration", 0) or 0),
                "remote_ip": request.get("remote_ip", ""),
            }
        )
    return list(reversed(values))


def traffic_summary(items: list[dict[str, Any]]) -> dict[str, Any]:
    hosts: dict[str, int] = {}
    statuses: dict[str, int] = {}
    for item in items:
        host = str(item.get("host") or "unknown")
        status = str(item.get("status") or "0")
        hosts[host] = hosts.get(host, 0) + 1
        statuses[status] = statuses.get(status, 0) + 1
    return {
        "requests": len(items),
        "hosts": sorted(hosts.items(), key=lambda pair: (-pair[1], pair[0]))[:8],
        "statuses": sorted(statuses.items(), key=lambda pair: pair[0]),
    }


def caddy_status(settings: Settings) -> dict[str, Any]:
    result: dict[str, Any] = {"admin": False, "error": ""}
    try:
        request = urllib.request.Request(f"{settings.caddy_admin_url}/config/", method="GET")
        with urllib.request.urlopen(request, timeout=5) as response:
            result["admin"] = response.status < 400
            response.read(1)
    except (urllib.error.URLError, TimeoutError) as exc:
        result["error"] = str(exc)
    return result


def probe_upstream(route: ManagedRoute, timeout: float) -> dict[str, Any]:
    if route.kind != RouteKind.PROXY or not route.upstreams:
        return {"ok": True, "detail": "not applicable"}
    address = route.upstreams[0].address
    url = address if address.startswith(("http://", "https://")) else f"http://{address}"
    try:
        request = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "caddy-ui-health/1.0"})
        context = ssl._create_unverified_context() if route.tls_skip_verify else None
        with urllib.request.urlopen(request, timeout=timeout, context=context) as response:
            return {"ok": response.status < 500, "status": response.status, "detail": f"HTTP {response.status}"}
    except urllib.error.HTTPError as exc:
        return {"ok": exc.code < 500, "status": exc.code, "detail": f"HTTP {exc.code}"}
    except Exception as exc:
        return {"ok": False, "status": 0, "detail": str(exc)}


def probe_public(route: ManagedRoute, timeout: float) -> dict[str, Any]:
    host = route.effective_host
    try:
        addresses = sorted({item[4][0] for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)})
    except OSError as exc:
        return {"ok": False, "status": 0, "addresses": [], "detail": f"DNS: {exc}"}
    context = ssl.create_default_context()
    try:
        request = urllib.request.Request(f"https://{host}/", method="HEAD", headers={"User-Agent": "caddy-ui-health/1.0"})
        with urllib.request.urlopen(request, timeout=timeout, context=context) as response:
            return {"ok": response.status < 500, "status": response.status, "addresses": addresses, "detail": f"HTTP {response.status}"}
    except urllib.error.HTTPError as exc:
        return {"ok": exc.code < 500, "status": exc.code, "addresses": addresses, "detail": f"HTTP {exc.code}"}
    except Exception as exc:
        return {"ok": False, "status": 0, "addresses": addresses, "detail": str(exc)}


def route_health(routes: list[ManagedRoute], settings: Settings) -> dict[str, dict[str, Any]]:
    enabled = [route for route in routes if route.enabled][: settings.reachability_limit]
    values: dict[str, dict[str, Any]] = {route.id: {} for route in enabled}
    with ThreadPoolExecutor(max_workers=min(12, max(1, len(enabled) * 2))) as executor:
        futures = {}
        for route in enabled:
            futures[executor.submit(probe_public, route, settings.reachability_timeout_seconds)] = (route.id, "public")
            futures[executor.submit(probe_upstream, route, settings.reachability_timeout_seconds)] = (route.id, "upstream")
        for future in as_completed(futures):
            route_id, kind = futures[future]
            try:
                values[route_id][kind] = future.result()
            except Exception as exc:
                values[route_id][kind] = {"ok": False, "detail": str(exc)}
    return values


def certificate_files(data_path: Path) -> list[dict[str, Any]]:
    now = dt.datetime.now(dt.UTC)
    certificates: list[dict[str, Any]] = []
    for path in data_path.glob("**/*.crt"):
        try:
            decoded = ssl._ssl._test_decode_cert(str(path))
            expiry = dt.datetime.strptime(decoded["notAfter"], "%b %d %H:%M:%S %Y %Z").replace(tzinfo=dt.UTC)
            subject = dict(item[0] for item in decoded.get("subject", []))
            san = [value for kind, value in decoded.get("subjectAltName", []) if kind == "DNS"]
            certificates.append(
                {
                    "name": subject.get("commonName", san[0] if san else path.stem),
                    "names": san,
                    "expires_at": expiry.isoformat(),
                    "days": (expiry - now).days,
                }
            )
        except (OSError, KeyError, ValueError, ssl.SSLError):
            continue
    return sorted(certificates, key=lambda item: (item["days"], item["name"]))
