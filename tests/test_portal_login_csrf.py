from __future__ import annotations

import io
import unittest
from http import HTTPStatus

from caddy_ui.public_auth import LOGIN_CSRF_COOKIE
from caddy_ui.public_auth_hotfix import PortalHandler


class PortalLoginCsrfRegressionTests(unittest.TestCase):
    def test_portal_login_reuses_existing_valid_csrf_token(self) -> None:
        token = "a" * 43
        handler = object.__new__(PortalHandler)
        handler._cookie = lambda name: token if name == LOGIN_CSRF_COOKIE else ""  # type: ignore[method-assign]
        handler._inject_login_csrf = lambda content, value: content + value.encode("ascii")  # type: ignore[method-assign]
        handler._login_csrf_cookie_header = lambda value: f"csrf={value}"  # type: ignore[method-assign]
        handler.send_response = lambda status: None  # type: ignore[method-assign]
        headers: list[tuple[str, str]] = []
        handler.send_header = lambda name, value: headers.append((name, value))  # type: ignore[method-assign]
        handler._security_headers = lambda: None  # type: ignore[method-assign]
        handler.end_headers = lambda: None  # type: ignore[method-assign]
        handler.wfile = io.BytesIO()

        handler._login_html(b"form:")

        self.assertEqual(handler.wfile.getvalue(), b"form:" + token.encode("ascii"))
        self.assertIn(("Set-Cookie", f"csrf={token}"), headers)

    def test_browser_metadata_does_not_trigger_hidden_portal_login(self) -> None:
        handler = object.__new__(PortalHandler)
        handler.headers = {"X-Forwarded-Uri": "/favicon.ico"}
        statuses: list[HTTPStatus] = []
        handler._empty = lambda status, **headers: statuses.append(status)  # type: ignore[method-assign]

        handler._portal_authorize(object())  # type: ignore[arg-type]

        self.assertEqual(statuses, [HTTPStatus.NOT_FOUND])

    def test_origin_null_requires_authenticated_non_cross_site_portal_hop(self) -> None:
        handler = object.__new__(PortalHandler)
        handler.headers = {
            "Origin": "null",
            "Sec-Fetch-Site": "same-origin",
            "X-Forwarded-Proto": "https",
            "X-Forwarded-Host": "julora.juloc.de",
        }
        handler.client_address = ("172.19.0.6", 12345)
        handler._proxy_allowed = lambda: True  # type: ignore[method-assign]

        self.assertTrue(handler._same_origin("https://julora.juloc.de"))

        handler.headers["Sec-Fetch-Site"] = "cross-site"
        self.assertFalse(handler._same_origin("https://julora.juloc.de"))

        handler.headers["Sec-Fetch-Site"] = "same-origin"
        handler._proxy_allowed = lambda: False  # type: ignore[method-assign]
        self.assertFalse(handler._same_origin("https://julora.juloc.de"))


if __name__ == "__main__":
    unittest.main()
