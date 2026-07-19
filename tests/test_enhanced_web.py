from __future__ import annotations

import http.client
import re
import tempfile
import threading
import unittest
import urllib.parse
from http.server import ThreadingHTTPServer
from pathlib import Path

from caddy_ui.enhanced_web import Application, create_handler
from caddy_ui.web import SESSION_COOKIE
from tests.helpers import settings


class EnhancedWebTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.app = Application(settings(Path(self.temporary.name)))
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), create_handler(self.app))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self) -> None:
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=3)
        self.temporary.cleanup()

    def request(self, method: str, path: str, body: dict[str, str] | None = None, cookie: str = ""):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=5)
        payload = urllib.parse.urlencode(body).encode("utf-8") if body is not None else None
        headers = {"Content-Type": "application/x-www-form-urlencoded"} if body is not None else {}
        if cookie:
            headers["Cookie"] = cookie
        connection.request(method, path, body=payload, headers=headers)
        response = connection.getresponse()
        content = response.read()
        result = response.status, dict(response.getheaders()), content
        connection.close()
        return result

    def login(self) -> str:
        status, headers, _ = self.request("POST", "/login", {"username": "admin", "password": "correct-horse-battery-staple"})
        self.assertEqual(status, 303)
        return headers["Set-Cookie"].split(";", 1)[0]

    def test_analytics_security_logs_and_settings_render(self) -> None:
        cookie = self.login()
        for path, expected in (
            ("/analytics", b"Analytics"),
            ("/analytics?tab=performance", b"Response time"),
            ("/logs", b"Request filters"),
            ("/security", b"Security"),
            ("/security?tab=limits", b"Per-route limits"),
            ("/admin/settings", b"Analytics retention"),
        ):
            with self.subTest(path=path):
                status, _, content = self.request("GET", path, cookie=cookie)
                self.assertEqual(status, 200)
                self.assertIn(expected, content)
                self.assertIn(b'href="/analytics"', content)
                self.assertIn(b'href="/security"', content)

    def test_saved_view_requires_csrf_and_can_be_created(self) -> None:
        cookie = self.login()
        status, _, page = self.request("GET", "/logs", cookie=cookie)
        self.assertEqual(status, 200)
        csrf = re.search(rb'name="csrf" value="([^"]+)"', page)
        self.assertIsNotNone(csrf)

        status, _, _ = self.request(
            "POST",
            "/saved-views/save",
            {"csrf": csrf.group(1).decode(), "kind": "logs", "name": "Errors", "query": '{"status":"5xx","range":"24h"}'},
            cookie,
        )
        self.assertEqual(status, 303)
        rows = self.app.analytics.saved_views(self.app.database.session(cookie.split("=", 1)[1])["user_id"], "logs")
        self.assertEqual(len(rows), 1)

    def test_viewer_can_view_but_not_export(self) -> None:
        user_id = self.app.users.save("viewer", "Viewer", __import__("caddy_ui.domain", fromlist=["Role"]).Role.VIEWER, "viewer-password")
        token, _ = self.app.database.create_session(user_id, 3600, "127.0.0.1", "test")
        cookie = f"{SESSION_COOKIE}={token}"
        status, _, _ = self.request("GET", "/analytics", cookie=cookie)
        self.assertEqual(status, 200)
        status, _, _ = self.request("GET", "/logs/export?format=csv", cookie=cookie)
        self.assertEqual(status, 403)


if __name__ == "__main__":
    unittest.main()
