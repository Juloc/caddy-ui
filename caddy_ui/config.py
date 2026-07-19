from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


def _boolean(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def _integer(name: str, default: int, minimum: int = 1) -> int:
    value = int(os.getenv(name, str(default)))
    return max(minimum, value)


@dataclass(frozen=True, slots=True)
class Settings:
    host: str
    port: int
    data_dir: Path
    database_path: Path
    backup_dir: Path
    caddyfile_path: Path
    routes_dir: Path
    caddy_data_path: Path
    access_log_path: Path
    caddy_log_path: Path
    caddy_admin_url: str
    default_domain: str
    auto_reload: bool
    session_ttl_seconds: int
    secure_cookies: bool
    bootstrap_username: str
    bootstrap_password: str
    legacy_config_path: Path
    reachability_timeout_seconds: float
    reachability_limit: int

    @classmethod
    def from_environment(cls) -> "Settings":
        data_dir = Path(os.getenv("CADDY_UI_DATA_DIR", "/var/lib/caddy-ui"))
        return cls(
            host=os.getenv("UI_HOST", "0.0.0.0"),
            port=_integer("UI_PORT", 8098),
            data_dir=data_dir,
            database_path=Path(os.getenv("CADDY_UI_DATABASE_PATH", str(data_dir / "caddy-ui.db"))),
            backup_dir=Path(os.getenv("CADDY_UI_BACKUP_DIR", str(data_dir / "backups"))),
            caddyfile_path=Path(os.getenv("CADDYFILE_PATH", "/etc/caddy/Caddyfile")),
            routes_dir=Path(os.getenv("CADDY_ROUTES_DIR", "/etc/caddy/routes")),
            caddy_data_path=Path(os.getenv("CADDY_DATA_PATH", "/data")),
            access_log_path=Path(os.getenv("CADDY_LOG_PATH", "/var/log/caddy/access.log")),
            caddy_log_path=Path(os.getenv("CADDY_SYSTEM_LOG_PATH", "/var/log/caddy/caddy.log")),
            caddy_admin_url=os.getenv("CADDY_ADMIN_URL", "http://caddy:2019").rstrip("/"),
            default_domain=os.getenv("DOMAIN", "").strip().rstrip("."),
            auto_reload=_boolean("CADDY_AUTO_RELOAD", True),
            session_ttl_seconds=_integer("CADDY_UI_SESSION_TTL", 86400, 300),
            secure_cookies=_boolean("CADDY_UI_SECURE_COOKIES", False),
            bootstrap_username=os.getenv("CADDY_UI_USERNAME", "admin").strip() or "admin",
            bootstrap_password=os.getenv("CADDY_UI_PASSWORD", ""),
            legacy_config_path=Path(os.getenv("CADDY_UI_CONFIG_PATH", "/etc/caddy/caddy-ui.json")),
            reachability_timeout_seconds=float(os.getenv("CADDY_UI_REACHABILITY_TIMEOUT", "3")),
            reachability_limit=_integer("CADDY_UI_REACHABILITY_LIMIT", 20),
        )

    def ensure_directories(self) -> None:
        self.data_dir.mkdir(parents=True, exist_ok=True)
        self.backup_dir.mkdir(parents=True, exist_ok=True)
        self.routes_dir.mkdir(parents=True, exist_ok=True)
