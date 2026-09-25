# Architecture

## Runtime

```text
Browser
  -> ASP.NET Core Razor Pages (ports 8098 and internal 8099)
      -> application services
          -> EF Core / PostgreSQL
          -> managed Caddy configuration and revisions
          -> DNS provider adapters and scheduled operations
          -> analytics, security and notification workers
          -> legacy SQLite migration reader
  -> Caddy reverse proxy
      -> Netcup DNS module
      -> Caddy UI request guard
      -> managed upstream routes
```

The production application uses .NET 10 with ASP.NET Core Razor Pages, EF Core and PostgreSQL 17. Go is limited to the custom Caddy binary and its modules. There is one supported application runtime and one production deployment path.

## Project boundaries

- `src/CaddyUi.Contracts`: transport and status contracts.
- `src/CaddyUi.Domain`: domain types, product metadata and invariants.
- `src/CaddyUi.Application`: route compilation, classification and application rules.
- `src/CaddyUi.Infrastructure`: PostgreSQL persistence, DNS providers, workers, file operations and cutover services.
- `src/CaddyUi.Web`: Razor Pages, authentication, authorization, UI and health endpoints.
- `src/CaddyUi.Migration`: idempotent read-only import from the legacy SQLite database.
- `tests/*`: unit, web, PostgreSQL, migration and acceptance tests.
- `caddyguard`: request protection and managed block feed support.
- `caddynetcp`: Netcup DNS module.
- `cmd/caddy`: custom Caddy entry point.

Presentation code must not call PostgreSQL, provider APIs or Caddy directly. Pages use application and infrastructure services through dependency injection.

## Persistence

PostgreSQL is the source of truth for users, domains, DNS providers, routes, revisions, operations, analytics, security events, jobs and system state. EF Core migrations run before application startup.

The previous SQLite file is mounted read-only during the transition. `CaddyUi.Migration` creates a backup, imports known records idempotently, preserves unknown legacy data in migration records and writes a report. SQLite is never used as the active runtime database after the migration.

ASP.NET Core Data Protection keys are persisted in PostgreSQL. Provider credentials entered through the UI are encrypted before persistence and are never returned to the browser. Advanced deployments may store environment-variable or file-secret references instead. Resolved plaintext values are materialized only in the protected runtime secret directory required by the Caddy DNS module.

## Route and certificate flow

1. A provider, domain and optional first route can be created atomically through the guided setup.
2. Saving changes updates PostgreSQL only; it does not change the active Caddy configuration.
3. The compiler creates a deterministic managed Caddy fragment, manifest and digest.
4. Wildcard and base-domain certificate plans are resolved separately. Unsupported or incomplete DNS-01 configurations block active apply instead of silently falling back.
5. Preview shows warnings and a line diff.
6. Apply writes a candidate, validates it, snapshots the previous fragment and atomically replaces the managed file.
7. The complete root Caddy configuration is validated before remote reload.
8. Failed apply or verification restores the previous snapshot and reloads it.

Existing unmanaged route files are preserved in `legacy-dotnet-cutover`. The initial managed fragment imports them until the first successful managed apply.

## Analytics and security

Caddy writes structured access and security logs to the shared log volume. Background workers ingest bounded batches into PostgreSQL with persistent checkpoints and rotation handling. Requests, pageviews, sessions, clients and technical assets remain separate metrics.

Sensitive headers, cookies, tokens and configured query values are redacted before persistence. IP intelligence and risk scoring are evidence-based operational signals, not identity claims. Managed IP blocks are written atomically to a dedicated data feed consumed by `caddy_ui_guard`; the feed is not imported as Caddyfile syntax.

## Deployment

The production stack contains:

- `postgres`: PostgreSQL 17 on an internal network.
- `migrate`: applies EF Core migrations.
- `legacy-import`: imports the read-only SQLite database idempotently.
- `config-init`: prepares and validates the reversible Caddy route bridge.
- `caddy`: custom Caddy bundle with DNS and guard modules.
- `caddy-ui`: Razor Pages UI, access portal and workers.

`Dockerfile.dotnet` is the only supported application image definition. It provides `dotnet-companion` and `dotnet-bundle` targets. The UI container has no Docker socket, drops Linux capabilities and exposes the access portal only inside Docker networking.

## Release flow

1. Update `VERSION_DOTNET`, `ProductMetadata.FoundationVersion`, the Docker build default and its test.
2. Merge only after .NET, Go, container, migration and production-contract checks pass.
3. The release workflow publishes immutable bundle and companion tags plus `latest`.
4. Published images are pulled and smoke-tested.
5. An annotated Git tag and GitHub Release are created.
6. The deployment workflow updates `Juloc/docker` with the exact version.
7. Server deployment keeps the legacy SQLite, Caddy configuration and PostgreSQL volumes for rollback until operational acceptance is complete.
