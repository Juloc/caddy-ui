from __future__ import annotations

import unittest

from caddy_ui.security import hash_password, new_totp_secret, totp_code, verify_password, verify_totp


class SecurityTests(unittest.TestCase):
    def test_password_hash_is_salted_and_verifiable(self) -> None:
        first = hash_password("correct-horse-battery-staple")
        second = hash_password("correct-horse-battery-staple")
        self.assertNotEqual(first, second)
        self.assertTrue(verify_password("correct-horse-battery-staple", first))
        self.assertFalse(verify_password("wrong-password", first))

    def test_totp_accepts_current_window(self) -> None:
        secret = new_totp_secret()
        code = totp_code(secret, timestamp=1_700_000_000)
        self.assertTrue(verify_totp(secret, code, timestamp=1_700_000_000))
        self.assertFalse(verify_totp(secret, "000000", timestamp=1_700_000_000))


if __name__ == "__main__":
    unittest.main()
