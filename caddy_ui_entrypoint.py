#!/usr/bin/env python3
import os
import sys
from pathlib import Path

from caddy_ui import __version__
from caddy_ui.caddy import DEFAULT_CADDYFILE



def init_caddyfile() -> int:
    caddyfile_path = Path(os.getenv("CADDYFILE_PATH", "/etc/caddy/Caddyfile"))
    routes_dir = Path(os.getenv("CADDY_ROUTES_DIR", "/etc/caddy/routes"))
    overwrite = os.getenv("CADDY_INIT_OVERWRITE", "false").lower() in {"1", "true", "yes"}

    routes_dir.mkdir(parents=True, exist_ok=True)
    placeholder = routes_dir / "site-00-placeholder.caddy"
    if not placeholder.exists():
        placeholder.write_text("# Route sites managed by caddy-ui.\n", encoding="utf-8", newline="\n")

    if caddyfile_path.exists() and not overwrite:
        current = caddyfile_path.read_text(encoding="utf-8")
        if current.strip():
            print(f"Keeping existing Caddyfile at {caddyfile_path}")
            return 0

    caddyfile_path.parent.mkdir(parents=True, exist_ok=True)
    caddyfile_path.write_text(DEFAULT_CADDYFILE, encoding="utf-8", newline="\n")
    print(f"Wrote Caddyfile to {caddyfile_path}")
    return 0


def run_caddy(args: list[str]) -> int:
    if not args:
        result = init_caddyfile()
        if result:
            return result
    if args:
        os.execv("/usr/bin/caddy", ["caddy", *args])
    config = os.getenv("CADDYFILE_PATH", "/etc/caddy/Caddyfile")
    os.execv("/usr/bin/caddy", ["caddy", "run", "--config", config, "--adapter", "caddyfile"])
    return 1


def main() -> int:
    command = sys.argv[1] if len(sys.argv) > 1 else "web"
    args = sys.argv[2:]
    print(f"Caddy UI v{__version__} starting (command={command})", flush=True)
    if command == "caddy":
        return run_caddy(args)
    if command == "web":
        from caddy_ui.web import main as web_main

        return web_main()
    if command == "init-caddyfile":
        return init_caddyfile()

    print(f"Unknown command: {command}", file=sys.stderr)
    print("Usage: caddy-ui [caddy|web|init-caddyfile]", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
