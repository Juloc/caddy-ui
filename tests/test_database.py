from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from caddy_ui.db import Database
from caddy_ui.domain import Role
from caddy_ui.repositories import UserRepository
from tests.helpers import settings


class DatabaseTests(unittest.TestCase):
    def test_bootstrap_admin_session_backup_and_restore(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            config = settings(Path(directory))
            database = Database(config)
            database.initialize()
            admin = database.authenticate("admin", "correct-horse-battery-staple")
            self.assertIsNotNone(admin)
            token, csrf = database.create_session(admin["id"], 3600, "127.0.0.1", "test")
            session = database.session(token)
            self.assertEqual(session["csrf_token"], csrf)
            database.set_setting("example", {"value": 1})
            backup = database.backup("test")
            database.set_setting("example", {"value": 2})
            database.restore(backup)
            self.assertEqual(database.setting("example"), {"value": 1})

    def test_last_administrator_cannot_be_disabled_or_demoted(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            database = Database(settings(Path(directory)))
            database.initialize()
            users = UserRepository(database)
            admin = users.list()[0]
            with self.assertRaises(ValueError):
                users.save(admin["username"], admin["display_name"], Role.ADMIN, "", admin["id"], False)
            with self.assertRaises(ValueError):
                users.save(admin["username"], admin["display_name"], Role.EDITOR, "", admin["id"], True)


if __name__ == "__main__":
    unittest.main()
