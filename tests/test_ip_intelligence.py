from __future__ import annotations

import unittest
from unittest.mock import patch

from caddy_ui.ip_intelligence import assess_client, clear_cache, lookup_ip


class IpIntelligenceTests(unittest.TestCase):
    def tearDown(self) -> None:
        clear_cache()

    @patch("caddy_ui.ip_intelligence._request_json")
    def test_private_address_does_not_call_external_provider(self, request_json) -> None:
        result = lookup_ip("192.168.1.26")

        self.assertTrue(result["available"])
        self.assertEqual(result["scope"], "private")
        self.assertEqual(result["holder"], "Local or special-purpose address")
        request_json.assert_not_called()

    @patch("caddy_ui.ip_intelligence._request_json")
    def test_public_address_returns_asn_holder_and_prefix(self, request_json) -> None:
        request_json.side_effect = (
            {"data": {"asns": [64500], "prefix": "203.0.113.0/24"}},
            {"data": {"holder": "Example Network", "block": {"name": "TEST", "desc": "Test registry"}}},
        )

        result = lookup_ip("8.8.8.8")

        self.assertTrue(result["available"])
        self.assertEqual(result["asn"], "AS64500")
        self.assertEqual(result["holder"], "Example Network")
        self.assertEqual(result["prefix"], "203.0.113.0/24")
        self.assertEqual(request_json.call_count, 2)

    def test_recognized_bot_is_not_automatically_high_risk(self) -> None:
        events = [
            {
                "client_type": "bot",
                "user_agent": "Googlebot/2.1",
                "path": "/articles/example",
                "uri": "/articles/example",
                "endpoint": "/articles/example",
                "status": 200,
                "occurred_at": f"2026-07-27T20:00:{index:02d}+00:00",
            }
            for index in range(10)
        ]

        result = assess_client(events)

        self.assertEqual(result["classification"], "Likely automated")
        self.assertEqual(result["risk"], "low")
        self.assertGreaterEqual(result["automation_score"], 60)

    def test_scanner_paths_and_errors_raise_abuse_risk(self) -> None:
        paths = ["/.env", "/.git/config", "/wp-login.php", "/phpmyadmin", "/random-1", "/random-2"]
        events = [
            {
                "client_type": "unknown",
                "user_agent": "",
                "path": path,
                "uri": path,
                "endpoint": path,
                "status": 404 if index < 5 else 403,
                "occurred_at": f"2026-07-27T20:00:{index:02d}+00:00",
            }
            for index, path in enumerate(paths)
        ]

        result = assess_client(events)

        self.assertIn(result["classification"], {"Likely automated", "Suspicious automation"})
        self.assertIn(result["risk"], {"medium", "high"})
        self.assertTrue(any("scanner" in reason for reason in result["reasons"]))


if __name__ == "__main__":
    unittest.main()
