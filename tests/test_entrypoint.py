from __future__ import annotations

import unittest

from caddy_ui.caddy import is_legacy_caddyfile


class EntrypointTests(unittest.TestCase):
    def test_known_legacy_caddyfile_is_detected(self) -> None:
        legacy = '''{
    admin 0.0.0.0:2019
}
{$DOMAIN}, *.{$DOMAIN} {
    import /etc/caddy/routes/*.caddy
    handle { respond "Service not configured" 404 }
}
'''
        self.assertTrue(is_legacy_caddyfile(legacy))

    def test_custom_caddyfile_is_never_modified(self) -> None:
        custom = "example.com { respond ok }\n"
        self.assertFalse(is_legacy_caddyfile(custom))


if __name__ == "__main__":
    unittest.main()
