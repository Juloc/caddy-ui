# Caddy UI

Caddy UI is a compact, server-rendered administration application for Caddy. Version 2 uses .NET 10, ASP.NET Core Razor Pages, EF Core and PostgreSQL. The interface follows the restrained AE01 Fluent/Windows 11 style: dark grouped navigation, dense workspaces, clear borders, light/dark themes and little JavaScript.

## Functions

- Admin authentication with administrator, editor and viewer roles
- LAN and public administration surfaces, TOTP and recovery codes
- Separate access portal for protected routes
- Domains, DNS providers, DNS records and DDNS jobs
- Proxy, redirect, static-response and optional custom routes
- Preview, diff, validation, atomic apply, verification and rollback
- Wildcard and individual certificates with Netcup DNS-01 support
- Request, pageview, session, client, performance and error analytics
- IP intelligence, risk assessment and managed IP blocks
- Healthchecks, scheduled jobs, notifications, backups and diagnostics
- Idempotent migration from the legacy SQLite application

## Architecture

The production stack contains:

| Service | Purpose |
| --- | --- |
| `postgres` | PostgreSQL 17 persistence |
| `migrate` | Applies the EF Core schema before startup |
| `legacy-import` | Imports the existing read-only SQLite database idempotently |
| `config-init` | Converts the legacy route layout into a reversible bridge |
| `caddy` | Caddy with Netcup DNS and integrated protection modules |
| `caddy-ui` | Razor Pages UI, portal, analytics and workers |

The application does not use the Docker socket. PostgreSQL is reachable only on an internal Docker network. The portal port `8099` is internal-only.

## Deployment

Copy the environment template, create the shared proxy network and start the stack:

```sh
cp .env.example .env
docker network create proxy
docker compose --env-file .env up -d
```

For the canonical versioned deployment use `deploy/docker-compose.yml`; the release workflow replaces `__CADDY_UI_VERSION__` with the stable version in the deployment repository.

Required secrets:

```env
CADDY_UI_DB_PASSWORD=long-random-value
CADDY_UI_PASSWORD=long-random-value
CADDY_UI_ADMIN_PROXY_SECRET=long-random-value
CADDY_UI_PORTAL_PROXY_SECRET=long-random-value
NETCUP_CUSTOMER_NUMBER=123456
NETCUP_API_KEY=secret
NETCUP_API_PASSWORD=secret
```

The admin UI is exposed on host port `8098`. The public origin defaults to `https://caddy.juloc.de` in the production template and can be changed through `CADDY_UI_PUBLIC_ORIGIN`.

## Legacy migration and rollback

The old `ui-data` volume is mounted read-only. On startup:

1. PostgreSQL migrations run.
2. `/legacy/caddy-ui.db` is imported idempotently when present.
3. Existing `site-*.caddy` files are moved to `legacy-dotnet-cutover`.
4. `site-managed-routes.caddy` initially imports those legacy files.
5. Caddy validates the complete configuration before starting.

The first successful managed route apply replaces only the bridge file. The existing Caddyfile, legacy route files and SQLite database remain available for rollback.

## Verification

```sh
dotnet restore CaddyUi.slnx
dotnet format CaddyUi.slnx --no-restore --verify-no-changes
dotnet build CaddyUi.slnx --configuration Release --no-restore
dotnet test CaddyUi.slnx --configuration Release --no-build
docker build --file Dockerfile.dotnet --target dotnet-bundle --tag caddy-ui:test .
```

CI additionally validates the production Compose model, migration path, login, health endpoints, bundled Caddy modules and reversible route preparation.

## Release

`VERSION_DOTNET` is the stable version source. A new stable version on `main` publishes:

- `ghcr.io/juloc/caddy-ui:<version>` and `latest`
- `ghcr.io/juloc/caddy-ui-companion:<version>` and `latest`

After successful smoke tests, the workflow creates the Git tag and release and opens a deployment PR in `Juloc/docker`.
