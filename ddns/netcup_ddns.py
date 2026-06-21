#!/usr/bin/env python3
import ipaddress
import json
import logging
import os
import sys
import time
import urllib.error
import urllib.request


NETCUP_ENDPOINT = os.getenv(
    "NETCUP_ENDPOINT",
    "https://ccp.netcup.net/run/webservice/servers/endpoint.php?JSON",
)


def env_required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"Missing required environment variable: {name}")
    return value


def parse_interval(value: str) -> int:
    value = value.strip().lower()
    if not value:
        return 300
    suffixes = {"s": 1, "m": 60, "h": 3600}
    if value[-1] in suffixes:
        return int(value[:-1]) * suffixes[value[-1]]
    return int(value)


def parse_hosts(value: str) -> list[str]:
    hosts = [host.strip() for host in value.split(",") if host.strip()]
    return hosts or ["@", "*"]


def normalize_hostname(hostname: str, domain: str) -> str:
    hostname = hostname.strip().rstrip(".")
    domain = domain.strip().rstrip(".")
    if hostname in {"", "@", domain}:
        return "@"
    suffix = f".{domain}"
    if hostname.endswith(suffix):
        hostname = hostname[: -len(suffix)]
    return hostname


def get_public_ip(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "caddy-ui-netcup-ddns/1.0"})
    with urllib.request.urlopen(request, timeout=20) as response:
        body = response.read(128).decode("utf-8").strip()
    ip = ipaddress.ip_address(body)
    if ip.version != 4:
        raise RuntimeError(f"Expected a public IPv4 address from {url}, got {ip}")
    return str(ip)


class NetcupClient:
    def __init__(self, customer_number: str, api_key: str, api_password: str):
        self.customer_number = customer_number
        self.api_key = api_key
        self.api_password = api_password
        self.session_id = ""

    def request(self, action: str, param: dict) -> dict:
        payload = json.dumps({"action": action, "param": param}).encode("utf-8")
        request = urllib.request.Request(
            NETCUP_ENDPOINT,
            data=payload,
            headers={
                "Content-Type": "application/json",
                "User-Agent": "caddy-ui-netcup-ddns/1.0",
            },
        )
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                raw = response.read().decode("utf-8")
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"Netcup HTTP {exc.code} during {action}: {body}") from exc

        data = json.loads(raw)
        if data.get("status") != "success":
            message = data.get("longmessage") or data.get("shortmessage") or raw
            raise RuntimeError(f"Netcup {action} failed: {message}")
        return data.get("responsedata") or {}

    def login(self) -> None:
        data = self.request(
            "login",
            {
                "customernumber": self.customer_number,
                "apikey": self.api_key,
                "apipassword": self.api_password,
            },
        )
        self.session_id = data["apisessionid"]

    def logout(self) -> None:
        if not self.session_id:
            return
        try:
            self.request(
                "logout",
                {
                    "customernumber": self.customer_number,
                    "apikey": self.api_key,
                    "apisessionid": self.session_id,
                },
            )
        finally:
            self.session_id = ""

    def dns_records(self, domain: str) -> list[dict]:
        data = self.request(
            "infoDnsRecords",
            {
                "domainname": domain,
                "customernumber": self.customer_number,
                "apikey": self.api_key,
                "apisessionid": self.session_id,
            },
        )
        records = data.get("dnsrecords", [])
        if isinstance(records, dict):
            records = [records]
        return records

    def update_dns_records(self, domain: str, records: list[dict]) -> None:
        self.request(
            "updateDnsRecords",
            {
                "domainname": domain,
                "customernumber": self.customer_number,
                "apikey": self.api_key,
                "apisessionid": self.session_id,
                "dnsrecordset": {"dnsrecords": records},
            },
        )


def update_once() -> bool:
    customer_number = env_required("NETCUP_CUSTOMER_NUMBER")
    api_key = env_required("NETCUP_API_KEY")
    api_password = env_required("NETCUP_API_PASSWORD")
    domain = (os.getenv("NETCUP_DDNS_DOMAIN") or env_required("DOMAIN")).strip().rstrip(".")
    hosts = {normalize_hostname(host, domain) for host in parse_hosts(os.getenv("NETCUP_DDNS_HOSTS", "@,*"))}
    public_ip_url = os.getenv("PUBLIC_IP_URL", "https://api64.ipify.org")
    record_type = os.getenv("NETCUP_DDNS_RECORD_TYPE", "A").strip().upper()

    if record_type != "A":
        raise RuntimeError("This updater currently supports IPv4 A records only.")

    public_ip = get_public_ip(public_ip_url)
    client = NetcupClient(customer_number, api_key, api_password)
    client.login()
    try:
        records = client.dns_records(domain)
        found = set()
        changed = []

        for record in records:
            hostname = normalize_hostname(str(record.get("hostname", "")), domain)
            rtype = str(record.get("type", "")).upper()
            if hostname not in hosts or rtype != record_type:
                continue
            found.add(hostname)
            old_destination = str(record.get("destination", "")).strip()
            if old_destination != public_ip:
                logging.info("Updating %s %s from %s to %s", hostname, record_type, old_destination, public_ip)
                record["destination"] = public_ip
                changed.append(hostname)

        missing = hosts - found
        if missing:
            missing_text = ", ".join(sorted(missing))
            raise RuntimeError(
                f"Missing Netcup {record_type} records for {domain}: {missing_text}. "
                "Create them once in Netcup before running the updater."
            )

        if not changed:
            logging.info("No DDNS update needed for %s; public IP is still %s", domain, public_ip)
            return False

        client.update_dns_records(domain, records)
        logging.info("Updated %s record(s) for %s to %s", len(changed), domain, public_ip)
        return True
    finally:
        client.logout()


def main() -> int:
    logging.basicConfig(
        level=os.getenv("LOG_LEVEL", "INFO").upper(),
        format="%(asctime)s %(levelname)s %(message)s",
    )
    interval = parse_interval(os.getenv("NETCUP_DDNS_INTERVAL", "300s"))
    run_once = os.getenv("NETCUP_DDNS_RUN_ONCE", "false").lower() in {"1", "true", "yes"}

    while True:
        try:
            update_once()
        except Exception:
            logging.exception("DDNS update failed")
            if run_once:
                return 1

        if run_once:
            return 0
        time.sleep(interval)


if __name__ == "__main__":
    sys.exit(main())
