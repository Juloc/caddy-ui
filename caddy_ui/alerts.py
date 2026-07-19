from __future__ import annotations

import json
import logging
import os
import urllib.parse
import urllib.request

from .notifications import NotificationService


class ExtendedNotificationService(NotificationService):
    """Notification service with opt-in Discord and Telegram adapters.

    Secrets are referenced by environment variable names and are never stored as
    literal tokens or webhook URLs in SQLite.
    """

    def create(
        self,
        severity: str,
        event_type: str,
        title: str,
        message: str,
        object_type: str = "",
        object_id: str = "",
    ) -> None:
        super().create(severity, event_type, title, message, object_type, object_id)
        for name, dispatcher in (("discord", self._dispatch_discord), ("telegram", self._dispatch_telegram)):
            try:
                dispatcher(event_type, severity, title, message)
            except Exception:
                logging.exception("%s notification failed", name.title())

    @staticmethod
    def _secret_from_env(name: str) -> str:
        name = name.strip()
        if not name or not name.replace("_", "").isalnum() or name.upper() != name:
            return ""
        return os.getenv(name, "")

    def _dispatch_discord(self, event_type: str, severity: str, title: str, message: str) -> None:
        if not self._enabled("discord", event_type):
            return
        settings = (self.database.setting("notifications", {}) or {}).get("discord", {})
        url = self._secret_from_env(str(settings.get("webhook_env", "")))
        if not url.startswith("https://"):
            return
        payload = json.dumps({"content": f"**[{severity.upper()}] {title}**\n{message}"}).encode("utf-8")
        request = urllib.request.Request(
            url,
            data=payload,
            headers={"Content-Type": "application/json", "User-Agent": "caddy-ui/1.0"},
        )
        with urllib.request.urlopen(request, timeout=10):
            pass

    def _dispatch_telegram(self, event_type: str, severity: str, title: str, message: str) -> None:
        if not self._enabled("telegram", event_type):
            return
        settings = (self.database.setting("notifications", {}) or {}).get("telegram", {})
        token = self._secret_from_env(str(settings.get("token_env", "")))
        chat_id = str(settings.get("chat_id", "")).strip()
        if not token or not chat_id:
            return
        url = f"https://api.telegram.org/bot{token}/sendMessage"
        payload = urllib.parse.urlencode(
            {"chat_id": chat_id, "text": f"[{severity.upper()}] {title}\n{message}"}
        ).encode("utf-8")
        request = urllib.request.Request(
            url,
            data=payload,
            headers={"Content-Type": "application/x-www-form-urlencoded", "User-Agent": "caddy-ui/1.0"},
        )
        with urllib.request.urlopen(request, timeout=10):
            pass
