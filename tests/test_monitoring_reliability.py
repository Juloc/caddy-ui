from __future__ import annotations

import socket
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from caddy_ui.domain import ManagedRoute, Upstream
from caddy_ui.monitoring import certificate_files, probe_public


class MonitoringReliabilityTests(unittest.TestCase):
    def test_public_probe_resolves_dns_and_checks_caddy_directly(self) -> None:
        route = ManagedRoute(name="app", domain="example.com", upstreams=[Upstream("app:8080")])
        addresses = [(socket.AF_INET, socket.SOCK_STREAM, 6, "", ("203.0.113.10", 443))]
        with patch("caddy_ui.monitoring.socket.getaddrinfo", return_value=addresses), patch(
            "caddy_ui.monitoring._probe_caddy_tls",
            return_value={"ok": True, "status": 502, "detail": "TLS valid, HTTP 502"},
        ) as tls_probe, patch(
            "caddy_ui.monitoring.urllib.request.urlopen", side_effect=AssertionError("public hairpin HTTPS request must not run")
        ):
            result = probe_public(route, 3, "caddy")

        self.assertTrue(result["ok"])
        self.assertEqual(result["status"], 502)
        self.assertEqual(result["addresses"], ["203.0.113.10"])
        self.assertIn("TLS valid", result["detail"])
        tls_probe.assert_called_once_with("caddy", "app.example.com", 3)

    def test_certificate_view_excludes_local_ca_expired_history_and_keeps_newest_current_certificate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            managed = root / "caddy" / "certificates" / "acme" / "example.com"
            managed.mkdir(parents=True)
            expired = managed / "expired.crt"
            older = managed / "older.crt"
            newer = managed / "newer.crt"
            expired.write_text("expired", encoding="utf-8")
            older.write_text("old", encoding="utf-8")
            newer.write_text("new", encoding="utf-8")
            local_ca = root / "caddy" / "pki" / "authorities" / "local" / "root.crt"
            local_ca.parent.mkdir(parents=True)
            local_ca.write_text("ca", encoding="utf-8")

            def decode(path: str) -> dict:
                if path.endswith("expired.crt"):
                    expiry = "Jan 01 00:00:00 2020 GMT"
                    name = "*.example.com"
                elif path.endswith("older.crt"):
                    expiry = "Jan 01 00:00:00 2030 GMT"
                    name = "example.com"
                elif path.endswith("newer.crt"):
                    expiry = "Jan 01 00:00:00 2031 GMT"
                    name = "example.com"
                else:
                    raise AssertionError("local CA certificate must not be inspected")
                return {
                    "notAfter": expiry,
                    "subject": ((("commonName", name),),),
                    "subjectAltName": (("DNS", name),),
                }

            with patch("caddy_ui.monitoring.ssl._ssl._test_decode_cert", side_effect=decode):
                certificates = certificate_files(root)

        self.assertEqual(len(certificates), 1)
        self.assertEqual(certificates[0]["name"], "example.com")
        self.assertEqual(certificates[0]["expires_at"], "2031-01-01T00:00:00+00:00")


if __name__ == "__main__":
    unittest.main()
