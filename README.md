# Caddy UI

Caddy UI is a compact, server-rendered administration tool for Caddy. It focuses on routes, access portals, logs, system health, DNS, and Netcup DDNS without Docker socket access or a heavy SPA.

The interface follows a restrained Fluent 2 / Windows 11 style. Dashboard summaries use cards; route, DNS, audit, and log workspaces stay flat and dense. It supports System, Light, and Dark themes and turns dialogs into full-screen views on mobile.

## Features

- Managed proxy, redirect, and administrator-only Custom Routes
- Multiple upstreams, load balancing, active health checks, paths, headers, and upstream TLS options
- Validate, preview/diff, apply, reload, verify, automatic rollback, and immutable revisions
- Enable, disable, duplicate, bulk delete, JSON import/export, and controlled unmanaged-snippet import
- Reusable branded username/password access portals
- Administrator, Editor, and Viewer roles; optional TOTP locally and required-by-default TOTP for public administration
- Dedicated Access, Logs, System, and DNS workspaces
- Netcup DNS record management and scheduled DDNS
- Public and upstream health shown separately
- SQLite persistence, audit history, traffic aggregation, daily backups, diagnostics, email, and webhook notifications

App templates and Docker management are intentionally not part of the product.

## Deployment

The default deployment uses exactly two containers and one bundle image:

| Container | Image | Purpose |
| --- | --- | --- |
| `caddy` | `ghcr.io/juloc/caddy-ui:<version>` | Caddy with the Netcup DNS module |
| `caddy-ui` | `ghcr.io/juloc/caddy-ui:<version>` | UI, authentication, DDNS, migration, aggregation, and backup jobs |

For an existing official or custom Caddy container, use `ghcr.io/juloc/caddy-ui-companion:<version>` for the UI container. Both modes keep the two-container boundary.

Create the external network and start the stack:

```sh
cp .env.example .env
python -c "import secrets; print(secrets.token_urlsafe(48))"
# Put the generated value in CADDY_UI_PROXY_SECRET.
docker network create proxy
docker compose --env-file .env up -d
```

Port `8098` is bound to `127.0.0.1` only. Do not change it to a public bind. Caddy's admin port `2019` must never be published.

## Initial local setup

Use an SSH tunnel for the first sign-in:

```sh
ssh -L 8098:127.0.0.1:8098 user@server
```

Open `http://127.0.0.1:8098`, sign in, replace any temporary password, and enable TOTP under Administration → Settings before enabling public administration.

New administrator and portal passwords must contain at least 14 characters. Passwords use salted scrypt hashes and are never rendered back to the browser.

## Safe public Caddy UI access

After TOTP is configured:

1. Set `CADDY_UI_PUBLIC_URL` to the exact HTTPS origin, for example `https://caddy.example.com`.
2. Keep the same generated `CADDY_UI_PROXY_SECRET` in both containers through Compose.
3. Restart the complete stack with `docker compose --env-file .env up -d`.
4. Caddy UI creates and validates the dedicated managed route to `caddy-ui:8098` when the configured host is unused.

Public mode rejects direct requests that did not pass through the trusted Caddy proxy. It validates the proxy secret, HTTPS scheme, configured host, Origin/Referer, CSRF tokens, session context, and persistent login limits. Secure, HttpOnly, SameSite=Strict cookies and HSTS are enabled automatically. Public administration requires TOTP unless explicitly disabled.

If the configured public host is already used by an incompatible route, startup fails rather than replacing it.

## Protecting another route with an access portal

1. Create an access group under **Access**.
2. Add one or more credentials with long unique passwords.
3. Select that access group on the managed route.
4. Preview and apply the route.

The generated site places the portal endpoint outside path-specific route matchers, strips client-supplied identity and internal-secret headers, and performs `forward_auth` through the internal Caddy UI service. Portal sessions expire after eight hours by default and are bound to the browser user agent. Rate limits are stored in SQLite and survive restarts.

## Required configuration

```env
ACME_EMAIL=admin@example.com
DOMAIN=example.com

NETCUP_CUSTOMER_NUMBER=123456
NETCUP_API_KEY=replace-me
NETCUP_API_PASSWORD=replace-me

CADDY_UI_USERNAME=admin
CADDY_UI_PASSWORD=use-a-long-unique-password
CADDY_UI_PROXY_SECRET=generated-url-safe-secret
CADDY_UI_PUBLIC_URL=
```

`CADDY_UI_PASSWORD` is required only when the first administrator is created. Provider records store environment-variable references, not the Netcup secret values.

`CADDY_UI_REQUIRE_TOTP=auto` requires TOTP whenever `CADDY_UI_PUBLIC_URL` is set. `CADDY_UI_BIND_SESSION_IP=true` additionally binds sessions to the client IP, but should only be enabled for clients with stable addresses.

## Authentication migration

The first hardened startup creates a database backup, adds persistent rate-limit and portal-session metadata, and invalidates old administrator and portal sessions. Existing password hashes remain verifiable; newly set passwords use the stronger current parameters.

## Persistence

| Volume | Content |
| --- | --- |
| `etc` | Root Caddyfile and generated site files |
| `data` | Caddy certificates and state |
| `config` | Caddy runtime configuration |
| `logs` | Rotated Caddy access logs |
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

## Development and verification

The runtime uses Python's standard library and a small dependency-free JavaScript file. The Caddy DNS provider is written in Go.

```sh
python -m compileall -q caddy_ui caddy_ui_entrypoint.py scripts tests
python -m unittest discover -v
gofmt -w cmd caddynetcp
go test ./...
docker build --target companion -t caddy-ui:companion-test .
docker build --target bundle -t caddy-ui:bundle-test .
```

The CI workflow performs these checks for pull requests and `main`.

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
