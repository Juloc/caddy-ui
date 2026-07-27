from __future__ import annotations

import urllib.parse

from . import web


_original_do_get = web.Handler.do_GET


def do_get(self) -> None:
    parsed = urllib.parse.urlsplit(self.path)
    if parsed.path == "/admin/settings":
        session = self._require_session(api=False)
        if not session:
            return
        query = urllib.parse.parse_qs(parsed.query)
        message = (query.get("message") or [""])[0]
        error = (query.get("error") or [""])[0]
        self._settings(session, message, error)
        return
    _original_do_get(self)


def install() -> None:
    web.Handler.do_GET = do_get
