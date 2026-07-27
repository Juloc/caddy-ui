from __future__ import annotations

import re
import unittest
from http import HTTPStatus

from caddy_ui.public_auth_hotfix import AdminHandler, stable_login_csrf_token


class LoginCsrfFaviconRegressionTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
