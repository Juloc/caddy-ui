from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from typing import Any


NETCUP_ENDPOINT = os.getenv("NETCUP_ENDPOINT", "https://ccp.netcup.net/run/webservice/servers/endpoint.php?JSON")


def normalize_hostname(hostname: str, domain: str) -> str:
    hostname = hostname.strip().rstrip(".")
    domain = domain.strip().rstrip(".")
    if hostname in {"", "@", domain}:
        return "@"
    suffix = f".{domain}"
    return hostname[: -len(suffix)] if hostname.endswith(suffix) else hostname


class NetcupClient:
    def __init__(self, customer_number: str, api_key: str, api_password: str):
        self.customer_number = customer_number
        self.api_key = api_key
        self.api_password = api_password
        self.session_id = ""

    def request(self, action: str, param: dict[str, Any]) -> dict[str, Any]:
        payload = json.dumps({"action": action, "param": param}).encode("utf-8")
        request = urllib.request.Request(
            NETCUP_ENDPOINT,
            data=payload,
            headers={"Content-Type": "application/json", "User-Agent": "caddy-ui-netcup/1.0"},
        )
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                raw = response.read(2 * 1024 * 1024).decode("utf-8")
        except urllib.error.HTTPError as exc:
            body = exc.read(4096).decode("utf-8", errors="replace")
            raise RuntimeError(f"Netcup HTTP {exc.code} during {action}: {body}") from exc
        data = json.loads(raw)
        if data.get("status") != "success":
            message = data.get("longmessage") or data.get("shortmessage") or "Unknown Netcup API error"
            raise RuntimeError(f"Netcup {action} failed: {message}")
        return data.get("responsedata") or {}

    def login(self) -> None:
        data = self.request("login", {"customernumber": self.customer_number, "apikey": self.api_key, "apipassword": self.api_password})
        self.session_id = str(data["apisessionid"])

    def logout(self) -> None:
        if not self.session_id:
            return
        try:
            self.request("logout", {"customernumber": self.customer_number, "apikey": self.api_key, "apisessionid": self.session_id})
        finally:
            self.session_id = ""

    def dns_records(self, domain: str) -> list[dict[str, Any]]:
        data = self.request("infoDnsRecords", {"domainname": domain, "customernumber": self.customer_number, "apikey": self.api_key, "apisessionid": self.session_id})
        records = data.get("dnsrecords", [])
        return [records] if isinstance(records, dict) else list(records)

    def update_dns_records(self, domain: str, records: list[dict[str, Any]]) -> None:
        self.request("updateDnsRecords", {"domainname": domain, "customernumber": self.customer_number, "apikey": self.api_key, "apisessionid": self.session_id, "dnsrecordset": {"dnsrecords": records}})


def expand_environment(value: str) -> str:
    value = str(value or "")
    if value.startswith("{env.") and value.endswith("}"):
        return os.getenv(value[5:-1], "")
    return value


class NetcupProvider:
    type = "netcup"

    def __init__(self, config: dict[str, Any]):
        self.config = config

    def client(self) -> NetcupClient:
        customer_number = expand_environment(str(self.config.get("customer_number", "")))
        api_key = expand_environment(str(self.config.get("api_key", "")))
        api_password = expand_environment(str(self.config.get("api_password", "")))
        if not all((customer_number, api_key, api_password)):
            raise ValueError("Netcup credentials are incomplete.")
        return NetcupClient(customer_number, api_key, api_password)

    def records(self, domain: str) -> list[dict[str, Any]]:
        client = self.client()
        client.login()
        try:
            records = client.dns_records(domain)
            return sorted(records, key=lambda item: (str(item.get("hostname", "")), str(item.get("type", ""))))
        finally:
            client.logout()

    def update(self, domain: str, records: list[dict[str, Any]]) -> None:
        client = self.client()
        client.login()
        try:
            client.update_dns_records(domain, records)
        finally:
            client.logout()

    def delete(self, domain: str, record: dict[str, Any]) -> None:
        value = dict(record)
        value["deleterecord"] = True
        self.update(domain, [value])

    def update_ddns(self, domain: str, hosts: list[str], public_ip: str) -> list[str]:
        normalized = {normalize_hostname(host, domain) for host in hosts}
        records = self.records(domain)
        changed: list[str] = []
        for record in records:
            hostname = normalize_hostname(str(record.get("hostname", "")), domain)
            if hostname in normalized and str(record.get("type", "")).upper() == "A":
                if str(record.get("destination", "")).strip() != public_ip:
                    record["destination"] = public_ip
                    changed.append(hostname)
        if normalized - {normalize_hostname(str(item.get("hostname", "")), domain) for item in records}:
            raise RuntimeError("One or more configured DDNS records do not exist.")
        if changed:
            self.update(domain, records)
        return changed
