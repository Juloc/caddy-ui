from __future__ import annotations

import http.client
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

from caddy_ui.secure_web import SecureApplication, create_secure_handler
from http.server import ThreadingHTTPServer
from tests.helpers import settings


class SecureWebTests(unittest.TestCase):
    def test_public_login_requires_proxy_secret_and_sets_strict_cookie(self) -> None:
        secret = "s" * 48
        environment = {
            "CADDY_UI_PUBLIC_URL": "https://caddy.example.com",
            "CADDY_UI_PROXY_SECRET": secret,
            "CADDY_UI_TRUSTED_PROXY_CIDRS": "127.0.0.0/8",
            "CADDY_UI_REQUIRE_TOTP": "true",
        }
        with tempfile.TemporaryDirectory() as directory, patch.dict(
            "os.environ", environment, clear=True
        ):
            application = SecureApplication(settings(Path(directory)))
            server = ThreadingHTTPServer(("127.0.0.1", 0), create_secure_handler(application))
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=5)
                connection.request("GET", "/login")
                direct = connection.getresponse()
                direct.read()
                self.assertEqual(direct.status, 404)
                connection.close()

                headers = {
                    "X-Caddy-UI-Proxy-Secret": secret,
                    "X-Forwarded-Proto": "https",
                    "X-Forwarded-Host": "caddy.example.com",
                    "X-Forwarded-For": "203.0.113.8",
                    "User-Agent": "security-test",
                }
                connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=5)
                connection.request("GET", "/login", headers=headers)
                proxied = connection.getresponse()
                body = proxied.read().decode("utf-8")
                self.assertEqual(proxied.status, 200)
                self.assertIn('name="login_csrf"', body)
                cookie = proxied.getheader("Set-Cookie", "")
                self.assertIn("HttpOnly", cookie)
                self.assertIn("SameSite=Strict", cookie)
                self.assertIn("Secure", cookie)
                self.assertEqual(
                    proxied.getheader("Strict-Transport-Security"),
                    "max-age=31536000; includeSubDomains",
                )
                connection.close()

                connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=5)
                post_headers = {
                    **headers,
                    "Content-Type": "application/x-www-form-urlencoded",
                }
                connection.request("POST", "/login", body="", headers=post_headers)
                cross_origin = connection.getresponse()
                cross_origin.read()
                self.assertEqual(cross_origin.status, 403)
                connection.close()
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=5)


if __name__ == "__main__":
    unittest.main()
