from __future__ import annotations

import re
import tempfile
import unittest
from dataclasses import replace
from email.message import Message
from http import HTTPStatus
from pathlib import Path
from types import SimpleNamespace

from caddy_ui.public_auth import ADMIN_PROXY_HEADER
from caddy_ui.public_auth_hotfix import (
    AdminHandler,
    local_admin_host,
    public_settings,
    stable_login_csrf_token,
)
from tests.helpers import settings


class LoginCsrfFaviconRegressionTests(unittest.TestCase):
    @staticmethod
    def handler(
        headers: dict[str, str],
        *,
        peer: str,
        public_origin: str = "https://caddy.juloc.de",
        secret: str = "proxy-secret",
    ) -> AdminHandler:
        handler = object.__new__(AdminHandler)
        message = Message()
        for name, value in headers.items():
            message[name] = value
        handler.headers = message
        handler.client_address = (peer, 12345)
        handler.app = SimpleNamespace(
            settings=SimpleNamespace(public_origin=public_origin),
            caddy=SimpleNamespace(admin_secret=secret),
        )
        return handler

    def test_valid_login_csrf_token_is_reused_across_parallel_login_gets(self) -> None:
        token = "a" * 43
        self.assertEqual(stable_login_csrf_token(token), token)
        self.assertEqual(stable_login_csrf_token(token), token)

    def test_invalid_or_missing_login_csrf_token_is_replaced(self) -> None:
        for value in ("", "short", "!" * 43):
            with self.subTest(value=value):
                token = stable_login_csrf_token(value)
                self.assertRegex(token, re.compile(r"^[A-Za-z0-9_-]{43}$"))
                self.assertNotEqual(token, value)

    def test_favicon_request_does_not_redirect_to_login_or_rotate_csrf(self) -> None:
        handler = object.__new__(AdminHandler)
        handler.path = "/favicon.ico"
        statuses: list[HTTPStatus] = []
        handler._empty = lambda status, **headers: statuses.append(status)  # type: ignore[method-assign]

        handler.do_GET()

        self.assertEqual(statuses, [HTTPStatus.NO_CONTENT])

    def test_private_ip_and_localhost_are_valid_direct_admin_hosts(self) -> None:
        for value in ("192.168.1.26:8098", "10.0.0.4", "127.0.0.1:8098", "[::1]:8098", "localhost:8098"):
            with self.subTest(value=value):
                self.assertTrue(local_admin_host(value))
        for value in ("caddy.juloc.de", "example.com:8098", "", "bad host"):
            with self.subTest(value=value):
                self.assertFalse(local_admin_host(value))

    def test_direct_lan_http_uses_local_origin_and_non_secure_cookie(self) -> None:
        handler = self.handler(
            {
                "Host": "192.168.1.26:8098",
                "Origin": "http://192.168.1.26:8098",
                "Sec-Fetch-Site": "same-origin",
            },
            peer="192.168.1.50",
        )

        self.assertTrue(handler._surface_allowed())
        self.assertEqual(handler._public_origin(), "")
        self.assertTrue(handler._same_origin("http://192.168.1.26:8098"))
        self.assertFalse(handler._secure_cookie())
        self.assertEqual(handler._cookie_name("session"), "session")

    def test_public_https_requires_authenticated_caddy_hop(self) -> None:
        without_secret = self.handler(
            {
                "Host": "caddy.juloc.de",
                "X-Forwarded-Host": "caddy.juloc.de",
                "X-Forwarded-Proto": "https",
                "Origin": "https://caddy.juloc.de",
            },
            peer="172.19.0.6",
        )
        self.assertFalse(without_secret._surface_allowed())

        via_caddy = self.handler(
            {
                "Host": "caddy.juloc.de",
                "X-Forwarded-Host": "caddy.juloc.de",
                "X-Forwarded-Proto": "https",
                ADMIN_PROXY_HEADER: "proxy-secret",
                "Origin": "https://caddy.juloc.de",
            },
            peer="172.19.0.6",
        )
        self.assertTrue(via_caddy._surface_allowed())
        self.assertEqual(via_caddy._public_origin(), "https://caddy.juloc.de")
        self.assertTrue(via_caddy._same_origin("https://caddy.juloc.de"))
        self.assertTrue(via_caddy._secure_cookie())
        self.assertEqual(via_caddy._cookie_name("session"), "__Host-session")

    def test_origin_null_is_allowed_only_for_non_cross_site_valid_surface(self) -> None:
        headers = {
            "Host": "caddy.juloc.de",
            "X-Forwarded-Host": "caddy.juloc.de",
            "X-Forwarded-Proto": "https",
            ADMIN_PROXY_HEADER: "proxy-secret",
            "Origin": "null",
            "Sec-Fetch-Site": "same-origin",
        }
        handler = self.handler(headers, peer="172.19.0.6")
        self.assertTrue(handler._same_origin("https://caddy.juloc.de"))

        headers["Sec-Fetch-Site"] = "cross-site"
        cross_site = self.handler(headers, peer="172.19.0.6")
        self.assertFalse(cross_site._same_origin("https://caddy.juloc.de"))

    def test_totp_is_forced_off_for_local_and_public_operation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            base = replace(
                settings(Path(directory)),
                public_origin="https://caddy.juloc.de",
                require_totp=True,
            )
            secured = public_settings(base)
            self.assertFalse(secured.require_totp)


if __name__ == "__main__":
    unittest.main()
