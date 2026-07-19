from __future__ import annotations

import http.client
import re
import tempfile
import threading
import unittest
import urllib.parse
from http.server import ThreadingHTTPServer
from pathlib import Path

from caddy_ui.domain import Role
from caddy_ui.web import Application, SESSION_COOKIE, create_handler
from tests.helpers import settings


class WebTests(unittest.TestCase):
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

    def test_health_login_session_and_csrf(self) -> None:
        status, headers, body = self.request("GET", "/api/health")
        self.assertEqual(status, 200)
        self.assertIn(b'"ok":true', body)
        self.assertIn("default-src 'self'", headers["Content-Security-Policy"])

        status, headers, _ = self.request("POST", "/login", {"username": "admin", "password": "correct-horse-battery-staple"})
        self.assertEqual(status, 303)
        cookie = headers["Set-Cookie"].split(";", 1)[0]
        self.assertTrue(cookie.startswith(f"{SESSION_COOKIE}="))

        status, _, page = self.request("GET", "/routes", cookie=cookie)
        self.assertEqual(status, 200)
        self.assertIn(b"New route", page)
        csrf = re.search(rb'name="csrf" value="([^"]+)"', page)
        self.assertIsNotNone(csrf)

        status, _, _ = self.request("POST", "/access/save", {"csrf": "wrong", "name": "private"}, cookie=cookie)
        self.assertEqual(status, 403)

        for path in ("/", "/access", "/logs", "/system", "/dns", "/admin/users", "/admin/audit", "/admin/settings"):
            with self.subTest(path=path):
                status, _, content = self.request("GET", path, cookie=cookie)
                self.assertEqual(status, 200)
                self.assertIn(b"Caddy UI", content)

    def test_viewer_cannot_modify_access_groups(self) -> None:
        user_id = self.app.users.save("viewer", "Viewer", Role.VIEWER, "viewer-password")
        token, csrf = self.app.database.create_session(user_id, 3600, "127.0.0.1", "test")
        cookie = f"{SESSION_COOKIE}={token}"
        status, _, _ = self.request("POST", "/access/save", {"csrf": csrf, "name": "private"}, cookie=cookie)
        self.assertEqual(status, 403)


if __name__ == "__main__":
    unittest.main()
