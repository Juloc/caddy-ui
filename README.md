# Caddy UI

Caddy UI is a compact, server-rendered administration application for Caddy. Version 2.1 uses .NET 10, ASP.NET Core Razor Pages, EF Core and PostgreSQL. The interface follows the restrained AE01 Fluent/Windows 11 style: grouped navigation, dense workspaces, clear borders, light/dark themes and progressive enhancement instead of a JavaScript-heavy frontend.

## Functions

- Admin authentication with administrator, editor and viewer roles
- LAN and public administration surfaces, TOTP and recovery codes
- Separate access portal for protected routes
- Guided setup for DNS provider, domain, certificates and an optional first route
- Provider-specific forms for Netcup and other supported DNS services
- Encrypted provider credentials using ASP.NET Data Protection
- DNS records and DDNS jobs with explicit connection tests
- Proxy, redirect, static-response and optional custom routes
- Preview, diff, validation, atomic apply, verification and rollback
- Separate wildcard and base-domain certificate plans with full Netcup DNS-01 support
- Request, pageview, session, client, performance and error analytics
- IP intelligence, risk assessment and managed IP blocks
- Healthchecks, scheduled jobs, notifications, backups and diagnostics
- Idempotent read-only migration from the legacy SQLite database

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

The application does not use the Docker socket. PostgreSQL is reachable only on an internal Docker network. The portal port `8099` is internal-only. `Dockerfile.dotnet` is the only supported application image definition.

## User interface

Caddy UI uses Microsoft Fluent 2 Web as its mandatory application-wide design system. The UI stays server-rendered with ASP.NET Core Razor Pages and semantic HTML; it does not use React, SPA navigation, or a client-rendered component tree. JavaScript and Fluent Web Components, when used, are progressive enhancement only and never a prerequisite for a management workflow.

Theme colors, status roles and elevation flow through one semantic `--ui-*` token alias layer; typography, spacing, radius and motion follow the documented Fluent ramps. The available themes are exactly System (default), Light, and Dark. The design contract defines the component, dialog, responsive, accessibility, and visual verification requirements in [docs/UI_DESIGN_CONTRACT.md](docs/UI_DESIGN_CONTRACT.md); the implementation rationale and official Microsoft sources are in [docs/DESIGN.md](docs/DESIGN.md).

## Deployment

Copy the environment template, create the shared proxy network and start the stack:

```sh
cp .env.example .env
docker network create proxy
docker compose --env-file .env up -d
```

For the canonical versioned deployment use `deploy/docker-compose.yml`; the release workflow replaces `__CADDY_UI_VERSION__` with the stable version in the deployment repository.

Required stack secrets:

```env
CADDY_UI_DB_PASSWORD=long-random-value
CADDY_UI_PASSWORD=long-random-value
CADDY_UI_ADMIN_PROXY_SECRET=long-random-value
CADDY_UI_PORTAL_PROXY_SECRET=long-random-value
```

DNS-provider credentials no longer need to be placed in the stack environment. Open **Configuration → Einrichtung** for the guided flow or **Configuration → DNS-Provider** for provider-only setup. Select Netcup to enter customer number, API key and API password. Secret fields are encrypted before PostgreSQL persistence and are never rendered back to the browser. Advanced deployments may still use `secret://env/NAME` or `secret://file/absolute/path` references.

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

CI additionally starts PostgreSQL and both application image targets, logs in through the real form and verifies `/Requests`, `/Access`, `/LiveLog`, `/Operations/Cutover`, `/Administration/Providers` and `/Setup`. It also validates the migration CLI, production Compose model, health endpoints, bundled Caddy modules and reversible route preparation.

## Release

`VERSION_DOTNET` is the stable version source. A new stable version on `main` publishes:

- `ghcr.io/juloc/caddy-ui:<version>` and `latest`
- `ghcr.io/juloc/caddy-ui-companion:<version>` and `latest`

After successful smoke tests, the workflow creates the Git tag and release and opens a deployment PR in `Juloc/docker`.
