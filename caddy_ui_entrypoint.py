#!/usr/bin/env python3
import os
import sys
from pathlib import Path


DEFAULT_CADDYFILE = """{
    email {$ACME_EMAIL}
    admin 0.0.0.0:2019
}

{$DOMAIN}, *.{$DOMAIN} {
    tls {
        dns netcup {
            customer_number {env.NETCUP_CUSTOMER_NUMBER}
            api_key {env.NETCUP_API_KEY}
            api_password {env.NETCUP_API_PASSWORD}
        }
        propagation_timeout 600s
        resolvers 1.1.1.1 8.8.8.8
    }

    encode zstd gzip

    log {
        output file /var/log/caddy/access.log {
            roll_size 10mb
            roll_keep 5
        }
        format json
    }

    import /etc/caddy/routes/*.caddy

    handle {
        respond "Service not configured" 404
    }
}
"""


def init_caddyfile() -> int:
    caddyfile_path = Path(os.getenv("CADDYFILE_PATH", "/etc/caddy/Caddyfile"))
    routes_dir = Path(os.getenv("CADDY_ROUTES_DIR", "/etc/caddy/routes"))
    overwrite = os.getenv("CADDY_INIT_OVERWRITE", "false").lower() in {"1", "true", "yes"}

    if not os.getenv("DOMAIN"):
        print("DOMAIN is required to initialize the wildcard Caddyfile.", file=sys.stderr)
        return 1

    routes_dir.mkdir(parents=True, exist_ok=True)
    placeholder = routes_dir / "00-placeholder.caddy"
    if not placeholder.exists():
        placeholder.write_text("# Route snippets managed by caddy-ui.\n", encoding="utf-8", newline="\n")

    if caddyfile_path.exists() and caddyfile_path.read_text(encoding="utf-8").strip() and not overwrite:
        print(f"Keeping existing Caddyfile at {caddyfile_path}")
        return 0

    caddyfile_path.parent.mkdir(parents=True, exist_ok=True)
    caddyfile_path.write_text(DEFAULT_CADDYFILE, encoding="utf-8", newline="\n")
    print(f"Wrote Caddyfile to {caddyfile_path}")
    return 0


def run_caddy(args: list[str]) -> int:
    if args:
        os.execv("/usr/bin/caddy", ["caddy", *args])
    config = os.getenv("CADDYFILE_PATH", "/etc/caddy/Caddyfile")
    os.execv("/usr/bin/caddy", ["caddy", "run", "--config", config, "--adapter", "caddyfile"])
    return 1


def main() -> int:
    command = sys.argv[1] if len(sys.argv) > 1 else "web"
    args = sys.argv[2:]
    if command == "caddy":
        return run_caddy(args)
    if command == "web":
        from ui.caddy_ui import main as web_main

        return web_main()
    if command == "ddns":
        from ddns.netcup_ddns import main as ddns_main

        return ddns_main()
    if command == "init-caddyfile":
        return init_caddyfile()

    print(f"Unknown command: {command}", file=sys.stderr)
    print("Usage: caddy-ui [caddy|web|ddns|init-caddyfile]", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
