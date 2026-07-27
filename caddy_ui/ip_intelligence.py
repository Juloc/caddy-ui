from __future__ import annotations

import ipaddress
import json
import threading
import time
import urllib.parse
import urllib.request
from datetime import datetime
from typing import Any, Iterable, Mapping


_CACHE_TTL_SECONDS = 24 * 60 * 60
_ERROR_CACHE_TTL_SECONDS = 10 * 60
_CACHE: dict[str, tuple[float, dict[str, Any]]] = {}
_CACHE_LOCK = threading.Lock()
_SCANNER_PATH_MARKERS = (
    "/.env",
    "/.git",
    "/actuator",
    "/cgi-bin",
    "/phpmyadmin",
    "/vendor/phpunit",
    "/wp-admin",
    "/wp-login",
    "/xmlrpc.php",
)


def _request_json(url: str, timeout: float = 3.0) -> dict[str, Any]:
    request = urllib.request.Request(
        url,
        headers={"Accept": "application/json", "User-Agent": "Caddy-UI IP intelligence"},
        method="GET",
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        if response.status != 200:
            raise RuntimeError(f"IP intelligence provider returned HTTP {response.status}.")
        payload = json.loads(response.read(1_000_001).decode("utf-8"))
    if not isinstance(payload, dict):
        raise RuntimeError("IP intelligence provider returned an invalid response.")
    return payload


def _address_scope(address: ipaddress._BaseAddress) -> str:
    if address.is_loopback:
        return "loopback"
    if address.is_private:
        return "private"
    if address.is_link_local:
        return "link-local"
    if address.is_multicast:
        return "multicast"
    if address.is_reserved:
        return "reserved"
    if not address.is_global:
        return "non-public"
    return "public"


def lookup_ip(value: str) -> dict[str, Any]:
    try:
        address = ipaddress.ip_address(value.strip())
    except ValueError:
        return {
            "ip": value.strip(),
            "available": False,
            "scope": "invalid",
            "error": "Invalid IP address.",
            "source": "local validation",
        }

    normalized = str(address)
    scope = _address_scope(address)
    if scope != "public":
        return {
            "ip": normalized,
            "available": True,
            "scope": scope,
            "asn": "",
            "prefix": "",
            "holder": "Local or special-purpose address",
            "registry": "",
            "source": "local validation",
            "cached": False,
        }

    now = time.monotonic()
    with _CACHE_LOCK:
        cached = _CACHE.get(normalized)
        if cached and cached[0] > now:
            return {**cached[1], "cached": True}

    result: dict[str, Any]
    ttl = _CACHE_TTL_SECONDS
    try:
        encoded = urllib.parse.quote(normalized, safe=":")
        network_response = _request_json(
            f"https://stat.ripe.net/data/network-info/data.json?resource={encoded}"
        )
        network_data = network_response.get("data") if isinstance(network_response.get("data"), dict) else {}
        asns = network_data.get("asns") if isinstance(network_data.get("asns"), list) else []
        asn = str(asns[0]) if asns else ""
        prefix = str(network_data.get("prefix") or "")
        holder = ""
        registry = ""
        registry_description = ""
        if asn:
            overview_response = _request_json(
                f"https://stat.ripe.net/data/as-overview/data.json?resource=AS{urllib.parse.quote(asn)}"
            )
            overview_data = overview_response.get("data") if isinstance(overview_response.get("data"), dict) else {}
            holder = str(overview_data.get("holder") or "")
            block = overview_data.get("block") if isinstance(overview_data.get("block"), dict) else {}
            registry = str(block.get("name") or "")
            registry_description = str(block.get("desc") or "")
        result = {
            "ip": normalized,
            "available": True,
            "scope": scope,
            "asn": f"AS{asn}" if asn else "",
            "prefix": prefix,
            "holder": holder or "Unknown network holder",
            "registry": registry,
            "registry_description": registry_description,
            "source": "RIPEstat",
            "cached": False,
        }
    except Exception as exc:
        ttl = _ERROR_CACHE_TTL_SECONDS
        result = {
            "ip": normalized,
            "available": False,
            "scope": scope,
            "asn": "",
            "prefix": "",
            "holder": "",
            "registry": "",
            "source": "RIPEstat",
            "error": str(exc),
            "cached": False,
        }

    with _CACHE_LOCK:
        _CACHE[normalized] = (now + ttl, result)
    return result


def assess_client(events: Iterable[Mapping[str, Any]]) -> dict[str, Any]:
    rows = list(events)
    if not rows:
        return {
            "classification": "Unknown",
            "automation_score": 0,
            "risk": "unknown",
            "reasons": ["No recent requests are available."],
        }

    total = len(rows)
    bot_count = 0
    empty_user_agents = 0
    not_found = 0
    auth_denied = 0
    scanner_hits = 0
    endpoints: set[str] = set()
    timestamps: list[datetime] = []

    for row in rows:
        client_type = str(row["client_type"] or "")
        user_agent = str(row["user_agent"] or "").strip()
        path = str(row["path"] if "path" in row.keys() else row["uri"] or "").lower()
        endpoint = str(row["endpoint"] or "")
        status = int(row["status"] or 0)
        occurred_at = str(row["occurred_at"] or "")
        bot_count += int(client_type == "bot")
        empty_user_agents += int(not user_agent)
        not_found += int(status == 404)
        auth_denied += int(status in {401, 403})
        scanner_hits += int(any(marker in path for marker in _SCANNER_PATH_MARKERS))
        if endpoint:
            endpoints.add(endpoint)
        try:
            timestamps.append(datetime.fromisoformat(occurred_at.replace("Z", "+00:00")))
        except ValueError:
            pass

    score = 0
    risk_score = 0
    reasons: list[str] = []
    bot_ratio = bot_count / total
    empty_ratio = empty_user_agents / total
    not_found_ratio = not_found / total

    if bot_ratio >= 0.5:
        score += 60
        reasons.append(f"{bot_count} of {total} requests use a recognized bot or automation user-agent.")
    elif bot_count:
        score += 30
        reasons.append(f"{bot_count} requests use a recognized bot or automation user-agent.")

    if empty_ratio >= 0.5:
        score += 25
        risk_score += 10
        reasons.append("Most requests do not provide a user-agent.")

    if scanner_hits >= 2:
        score += 30
        risk_score += 50
        reasons.append(f"{scanner_hits} requests target common scanner or exploit-probe paths.")

    if not_found_ratio >= 0.5 and len(endpoints) >= 10:
        score += 20
        risk_score += 30
        reasons.append(f"High 404 rate across {len(endpoints)} different endpoints.")

    if auth_denied >= 5:
        score += 15
        risk_score += 35
        reasons.append(f"{auth_denied} authorization failures were observed.")

    if len(timestamps) >= 2:
        span_seconds = max(1.0, (max(timestamps) - min(timestamps)).total_seconds())
        requests_per_minute = total / span_seconds * 60
        if requests_per_minute >= 60:
            score += 15
            risk_score += 25
            reasons.append(f"Request rate is approximately {requests_per_minute:.0f} per minute.")

    score = min(100, score)
    risk_score = min(100, risk_score)
    if score >= 70:
        classification = "Likely automated"
    elif score >= 40:
        classification = "Suspicious automation"
    elif score >= 20:
        classification = "Possibly automated"
    elif empty_user_agents == total:
        classification = "Unknown client"
    else:
        classification = "Probably human"

    if risk_score >= 60:
        risk = "high"
    elif risk_score >= 25:
        risk = "medium"
    else:
        risk = "low"

    if not reasons:
        reasons.append("No strong automation or abuse indicators were found in the recent sample.")

    return {
        "classification": classification,
        "automation_score": score,
        "risk": risk,
        "reasons": reasons,
    }


def clear_cache() -> None:
    with _CACHE_LOCK:
        _CACHE.clear()
