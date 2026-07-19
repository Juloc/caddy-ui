from __future__ import annotations

from dataclasses import replace
from pathlib import Path

from caddy_ui.config import Settings


def settings(tmp_path: Path) -> Settings:
    data = tmp_path / "data"
    routes = tmp_path / "routes"
    caddyfile = tmp_path / "Caddyfile"
    caddyfile.write_text("import routes/*.caddy\n", encoding="utf-8")
    return Settings(
        host="127.0.0.1",
        port=0,
        data_dir=data,
        database_path=data / "app.db",
        backup_dir=data / "backups",
        caddyfile_path=caddyfile,
        routes_dir=routes,
        caddy_data_path=tmp_path / "caddy-data",
        access_log_path=tmp_path / "access.log",
        caddy_log_path=tmp_path / "caddy.log",
        caddy_admin_url="http://127.0.0.1:1",
        default_domain="example.com",
        auto_reload=False,
        session_ttl_seconds=3600,
        secure_cookies=False,
        bootstrap_username="admin",
        bootstrap_password="correct-horse-battery-staple",
        legacy_config_path=tmp_path / "legacy.json",
        reachability_timeout_seconds=0.1,
        reachability_limit=20,
    )
