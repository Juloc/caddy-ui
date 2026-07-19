from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from caddy_ui.monitoring import parse_access_logs


class AccessLogParsingTests(unittest.TestCase):
    def test_query_parameters_are_redacted_without_urlencode_type_error(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "access.log"
            path.write_text(
                json.dumps(
                    {
                        "ts": 1,
                        "request": {
                            "host": "example.com",
                            "method": "GET",
                            "uri": "/callback?token=secret-value&name=test",
                            "remote_ip": "127.0.0.1",
                        },
                        "status": 200,
                        "size": 12,
                        "duration": 0.01,
                    }
                )
                + "\n",
                encoding="utf-8",
            )

            items = parse_access_logs(path)

        self.assertEqual(len(items), 1)
        self.assertEqual(items[0]["uri"], "/callback?token=%5Bredacted%5D&name=test")


if __name__ == "__main__":
    unittest.main()
