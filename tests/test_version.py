from __future__ import annotations

import unittest

from scripts.next_version import next_version


class VersionTests(unittest.TestCase):
    def test_prerelease_default_and_promotion(self) -> None:
        self.assertEqual(next_version("1.0.0-alpha.1", set()), "1.0.0-alpha.2")
        self.assertEqual(next_version("1.0.0-alpha.2", {"release:beta"}), "1.0.0-beta.1")
        self.assertEqual(next_version("1.0.0-beta.4", {"release:stable"}), "1.0.0")

    def test_stable_patch_minor_and_major(self) -> None:
        self.assertEqual(next_version("1.2.3", set()), "1.2.4")
        self.assertEqual(next_version("1.2.3", {"minor"}), "1.3.0")
        self.assertEqual(next_version("1.2.3", {"release:major"}), "2.0.0")


if __name__ == "__main__":
    unittest.main()
