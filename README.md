# Caddy UI

Caddy UI is a compact, server-rendered administration tool for Caddy. It focuses on routes, access portals, analytics, security, logs, system health, DNS, and Netcup DDNS without Docker socket access or a heavy SPA.

The interface follows a restrained Fluent 2 / Windows 11 style. Dashboard summaries use cards; route, DNS, audit, analytics, security, and log workspaces stay flat and dense. It supports System, Light, and Dark themes and turns dialogs into full-screen views on mobile.

## Features

- Managed proxy, redirect, and administrator-only Custom Routes
- Multiple upstreams, load balancing, active health checks, paths, headers, and upstream TLS options
- Validate, preview/diff, apply, reload, verify, automatic rollback, and immutable revisions
- Enable, disable, duplicate, bulk delete, JSON import/export, and controlled unmanaged-snippet import
- Reusable branded username/password access portals with isolated forward-auth handling
- Administrator, Editor, and Viewer roles; optional or required TOTP; CSRF-protected server-side sessions
- Request analytics, performance views, client classification, live logs, saved views, and exports
- Integrated route protection, progressive login protection, IP blocks, alerts, and observability jobs
- Dedicated Access, Analytics, Security, Logs, System, and DNS workspaces
- Netcup DNS record management and scheduled DDNS
- Public and upstream health shown separately
- SQLite persistence, audit history, traffic aggregation, daily backups, diagnostics, email, webhook, Discord, and Telegram notifications

App templates and Docker management are intentionally not part of the product.

## Deployment

The default deployment uses exactly two containers and one bundle image:

| Container | Image | Purpose |
| --- | --- | --- |
| `caddy` | `ghcr.io/juloc/caddy-ui:<version>` | Caddy with the Netcup DNS and integrated protection modules |
| `caddy-ui` | `ghcr.io/juloc/caddy-ui:<version>` | UI, access portal, analytics, security, DDNS, migration, aggregation, and backup jobs |

For an existing official or custom Caddy container, use `ghcr.io/juloc/caddy-ui-companion:<version>` for the UI container. Both modes keep the two-container boundary. Companion mode keeps analytics and login protection but cannot apply the bundled Caddy route-guard directive.

Create the external network and start the stack:

```sh
cp .env.example .env
docker network create proxy
docker compose --env-file .env up -d
```

The administration listener is bound to host loopback at `127.0.0.1:8098`. The access-portal listener on port `8099` is internal-only. Never publish port `8099` or Caddy's admin port `2019`.

For initial setup, use an SSH tunnel:

```sh
ssh -L 8098:127.0.0.1:8098 your-server
```

Then open `http://127.0.0.1:8098` locally.

## Public administration UI

The administration UI can be exposed through Caddy after initial setup:

1. Enable TOTP for the administrator under **Administration → Settings**.
2. Set `CADDY_UI_PUBLIC_ORIGIN` to the exact external HTTPS origin, for example `https://caddy.example.com`.
3. Create an unprotected managed proxy route for that host with upstream `caddy-ui:8098`. Do not assign an Access Group because the administration UI has its own login.
4. Restart `caddy-ui`.
5. After every required account has TOTP configured, set `CADDY_UI_REQUIRE_TOTP=true`.

Public mode rejects non-HTTPS requests and requests for a different external host. Session cookies use `Secure`, `HttpOnly`, `SameSite`, `Path=/`, and the `__Host-` prefix. Login POSTs require a same-origin browser request, sessions are bound to the browser user agent, and progressive login protection persists in SQLite.

## Access portal security

Protected routes use a separate internal listener and a generated Caddy-to-portal secret. Portal paths are handled before application path matchers, so path-based routes cannot recursively protect their own login page.

- Each Access Group has independent credentials and sessions.
- Portal sessions expire after `CADDY_UI_PORTAL_SESSION_TTL`, defaulting to 12 hours.
- Session tokens are stored only as hashes and are bound to the browser user agent.
- The reserved path `/__caddy_ui_auth/*` cannot be assigned to a managed route.
- External and recursive return targets are rejected.
- Incoming `Remote-User` and internal portal identity headers are removed or overwritten before managed upstreams.
- Brute-force protection applies per client and identity and persists across restarts.
- Password length, accepted scrypt parameters, hash concurrency, and HTTP worker concurrency are bounded.
- Existing managed route files are automatically regenerated through the hardened renderer during startup.

## Required configuration

```env
ACME_EMAIL=admin@example.com
DOMAIN=example.com

NETCUP_CUSTOMER_NUMBER=123456
NETCUP_API_KEY=replace-me
NETCUP_API_PASSWORD=replace-me

CADDY_UI_USERNAME=admin
CADDY_UI_PASSWORD=use-a-long-unique-password
```

`CADDY_UI_PASSWORD` is required only when the first administrator is created. Passwords are stored as salted scrypt hashes. Provider records store environment-variable references, not the Netcup secret values.

Use `CADDY_UI_PUBLIC_ORIGIN` for public HTTPS access. `CADDY_UI_SECURE_COOKIES=true` can force secure cookies without a configured public origin. `DOMAIN` provides the default domain but is optional after domains are configured in the UI.

## Persistence

| Volume | Content |
| --- | --- |
| `etc` | Root Caddyfile, generated site files, and security blocklist |
| `data` | Caddy certificates and state |
| `config` | Caddy runtime configuration |
| `logs` | Rotated access, system, and security logs |
| `ui-data` | SQLite database and backups |

The UI database uses WAL mode, foreign keys, explicit transactions, and integrity-checked backups. Traffic stays hourly for 30 days, then daily for one year, then monthly without an automatic expiry.

## Upgrading from the legacy UI

On first start Caddy UI:

1. creates a pre-migration database backup when applicable;
2. imports legacy provider JSON and route metadata once;
3. recognizes the pre-1.0 generated wildcard Caddyfile;
4. saves it as `Caddyfile.pre-1.0`; and
5. replaces only that recognized generated shape with the new `site-*.caddy` managed-site import.

Custom Caddyfiles and unmanaged snippets are never overwritten. They are not included by the new managed-only import; import their route directives through the administrator-only preview wizard instead.

Existing portal sessions are intentionally invalidated by the hardened user-agent binding. Managed protected routes are reconciled automatically; no manual reapply is required.

## Development and verification

The runtime uses Python's standard library and a small dependency-free JavaScript file. Caddy extensions are written in Go.

```sh
python -m compileall -q caddy_ui caddy_ui_entrypoint.py scripts tests
python -m unittest discover -v
gofmt -w cmd caddyguard caddynetcp
go test ./...
docker build --target companion -t caddy-ui:companion-test .
docker build --target bundle -t caddy-ui:bundle-test .
```

The CI workflow performs these checks for pull requests and `main` and verifies that the bundle contains the integrated protection module.

## Releases

A successful merge to `main` creates the next SemVer release and publishes both images. During the pre-1.0 phase, releases advance `alpha.N` by default.

| Pull request label | Result |
| --- | --- |
| none | next patch, or next current prerelease sequence |
| `minor` / `release:minor` | next minor version |
| `major` / `release:major` | next major version |
| `beta` / `release:beta` | promote to or advance beta |
| `stable` / `release:stable` | publish the stable base version |

After both images build successfully, the workflow creates the GitHub Release and opens an auto-merge PR updating `Juloc/docker/caddy/docker-compose.yml`. Repository secret `DOCKER_REPO_TOKEN` must have access to that private repository.

Detailed decisions and verification status are in [`docs/`](docs/).
