from __future__ import annotations

import json
import logging
import os
import smtplib
import urllib.request
from email.message import EmailMessage
from typing import Any

from .db import Database, utc_now


class NotificationService:
    def __init__(self, database: Database):
        self.database = database

    def create(
        self,
        severity: str,
        event_type: str,
        title: str,
        message: str,
        object_type: str = "",
        object_id: str = "",
    ) -> None:
        with self.database.transaction() as connection:
            connection.execute(
                "INSERT INTO notifications(created_at,severity,event_type,title,message,object_type,object_id) VALUES(?,?,?,?,?,?,?)",
                (utc_now(), severity, event_type, title, message, object_type, object_id),
            )
        try:
            self._dispatch_webhook(event_type, severity, title, message, object_type, object_id)
        except Exception:
            logging.exception("Webhook notification failed")
        try:
            self._dispatch_email(event_type, severity, title, message)
        except Exception:
            logging.exception("Email notification failed")

    def unacknowledged(self, limit: int = 50):
        with self.database.connect() as connection:
            return connection.execute(
                "SELECT * FROM notifications WHERE acknowledged_at IS NULL ORDER BY created_at DESC LIMIT ?",
                (min(max(limit, 1), 200),),
            ).fetchall()

    def acknowledge(self, notification_id: int) -> None:
        with self.database.transaction() as connection:
            connection.execute("UPDATE notifications SET acknowledged_at=? WHERE id=?", (utc_now(), notification_id))

    def acknowledge_matching(self, event_type: str, object_type: str = "", object_id: str = "") -> None:
        with self.database.transaction() as connection:
            connection.execute(
                """UPDATE notifications SET acknowledged_at=?
                   WHERE acknowledged_at IS NULL AND event_type=? AND object_type=? AND object_id=?""",
                (utc_now(), event_type, object_type, object_id),
            )

    def acknowledge_event_type(self, event_type: str) -> None:
        with self.database.transaction() as connection:
            connection.execute(
                "UPDATE notifications SET acknowledged_at=? WHERE acknowledged_at IS NULL AND event_type=?",
                (utc_now(), event_type),
            )

    def _enabled(self, channel: str, event_type: str) -> bool:
        settings = self.database.setting("notifications", {}) or {}
        channel_settings = settings.get(channel, {}) if isinstance(settings, dict) else {}
        events = channel_settings.get("events", ["*"])
        return bool(channel_settings.get("enabled")) and ("*" in events or event_type in events)

    def _dispatch_webhook(self, event_type: str, severity: str, title: str, message: str, object_type: str, object_id: str) -> None:
        if not self._enabled("webhook", event_type):
            return
        settings = self.database.setting("notifications", {}).get("webhook", {})
        url = str(settings.get("url", ""))
        if not url.startswith(("http://", "https://")):
            return
        payload = json.dumps(
            {
                "event": event_type,
                "severity": severity,
                "title": title,
                "message": message,
                "object_type": object_type,
                "object_id": object_id,
                "timestamp": utc_now(),
            }
        ).encode("utf-8")
        request = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json", "User-Agent": "caddy-ui/1.0"})
        with urllib.request.urlopen(request, timeout=10):
            pass

    def _dispatch_email(self, event_type: str, severity: str, title: str, message: str) -> None:
        if not self._enabled("email", event_type):
            return
        settings = self.database.setting("notifications", {}).get("email", {})
        email = EmailMessage()
        email["Subject"] = f"[Caddy UI] [{severity.upper()}] {title}"
        email["From"] = settings.get("from", "caddy-ui@localhost")
        email["To"] = settings.get("to", "")
        email.set_content(message)
        with smtplib.SMTP(settings.get("host", "localhost"), int(settings.get("port", 25)), timeout=10) as client:
            if settings.get("starttls"):
                client.starttls()
            if settings.get("username"):
                client.login(settings["username"], os.getenv(str(settings.get("password_env", "")), ""))
            client.send_message(email)
