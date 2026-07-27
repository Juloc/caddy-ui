# Architecture

## Runtime

```text
Browser
  -> Caddy UI HTTP application
      -> application services
          -> SQLite repositories
          -> managed Caddy configuration
          -> Caddy admin API
          -> provider adapters
          -> analytics/security collectors
          -> notification adapters
  -> Caddy reverse proxy
      -> Caddy UI request guard (bundle mode)
          -> managed reverse-proxy routes
```

The application remains a server-rendered Python service with a small amount of dependency-free JavaScript. Go remains limited to the custom Caddy build, the Netcup DNS module, and the lightweight Caddy UI request guard. Core operation requires no additional database, cache, analytics, or security container.

## Module boundaries

- `caddy_ui/config.py`: environment and path settings.
- `caddy_ui/db.py`: SQLite connection, core migrations, transactions, backup primitives.
- `caddy_ui/domain.py`: typed domain objects and validation.
- `caddy_ui/repositories.py`: persistence access.
- `caddy_ui/security.py`: password hashing, sessions, CSRF, TOTP, permissions.
- `caddy_ui/audit.py`: append-only redacted audit events.
- `caddy_ui/caddy.py`: route rendering, import, validation, reload, revisions, rollback.
- `caddy_ui/protection.py`: persistent login protection, threat events, temporary restrictions, blocklist synchronization, and security-aware managed-route rendering.
- `caddy_ui/analytics.py`: request ingestion, redaction, endpoint normalization, structured filters, percentiles, rollups, retention, saved views, and exports.
- `caddy_ui/observability.py`: bounded background analytics ingestion, security-event ingestion, threat scans, retention, and blocklist refresh.
- `caddy_ui/providers/`: provider contracts and Netcup implementation.
- `caddy_ui/monitoring.py`: health probes, certificates, legacy log parsing, and operational aggregation.
- `caddy_ui/notifications.py`: dashboard, email, and generic webhook dispatch.
- `caddy_ui/alerts.py`: opt-in Discord and Telegram adapters using environment-referenced secrets.
- `caddy_ui/web.py` and `views.py`: stable base request routing and existing workspaces.
- `caddy_ui/enhanced_web.py`, `analytics_views.py`, `security_views.py`, and `static/`: analytics/security routing, presentation, live SSE, and responsive assets.
- `caddy_ui/jobs.py`: DDNS, legacy traffic aggregation, retention, health checks, and backup schedules.
- `caddyguard/`: custom Caddy HTTP handler for request-rate limiting, trusted-proxy client resolution, dynamic temporary restrictions, and security-event logging.

Presentation code must not call SQLite, provider APIs, or Caddy directly. Views receive already prepared values from the request/application layer.

## Request analytics data flow

1. Caddy writes structured JSON access logs to the shared log volume.
2. The companion tails a bounded recent window every 15 seconds.
3. Each source line is hashed so repeated scans are idempotent.
4. Sensitive query values are redacted before SQLite persistence.
5. Exact paths and normalized endpoints are stored separately.
6. Hourly aggregate buckets are updated transactionally as new raw events are inserted.
7. Raw data older than the configured minimum 30-day window is deleted after hourly data has been compacted into daily aggregates.
8. Aggregates expire according to the configured retention, one year by default.

Raw request tables never persist request/response bodies, cookies, or Authorization headers.

## Security data flow

### Bundle mode

1. Managed route rendering injects `caddy_ui_guard` into eligible managed route handlers.
2. Caddy enforces per-client token-bucket limits before reverse proxying.
3. Explicit trusted-proxy networks control whether forwarded client-IP headers may be used.
4. A shared, atomically replaced blocklist file carries temporary administrator/automatic restrictions from the companion to Caddy without requiring a reload for each change.
5. The guard writes bounded JSON security events to the shared log volume.
6. The companion ingests those events into SQLite for auditability and UI drill-downs.
7. Threat detection uses already persisted request metadata and may create only temporary automatic restrictions.

### Companion mode

The companion may manage a Caddy build without `caddy_ui_guard`. Security-aware configuration activation must validate through Caddy's admin API. Unsupported directives are rolled back and surfaced as a warning; existing working routes must not be left with unsupported configuration.

## Configuration ownership

- Managed routes are stored in SQLite as the source of truth and rendered deterministically to individual snippets.
- Custom routes are stored separately and are administrator-only.
- Unmanaged files are never silently changed.
- Every applied route/security configuration has an immutable revision manifest and content digest.
- Secrets are excluded from diffs, audit payloads, exports, and diagnostic bundles.
- Security settings live in SQLite; temporary block state is mirrored to the shared blocklist file as derived runtime state.

## Persistence and migrations

Analytics/security feature schemas are additive to the core SQLite schema. Before the first feature-schema migration, Caddy UI creates an automatic database backup, applies the schema transactionally, and verifies `PRAGMA integrity_check`. Failed migration or configuration activation preserves rollback capability.

SQLite remains in WAL mode with targeted indexes on time, host, endpoint, IP, status, and response-time dimensions. The observability loop uses bounded log reads and batched SQLite transactions so request processing is not on the Caddy critical path.

## Deployment

### Bundle

- `ghcr.io/juloc/caddy-ui:<version>` contains both runtimes and is started once as `caddy` and once as `caddy-ui`.
- The custom Caddy binary contains the standard modules, Netcup DNS module, and `caddy_ui_guard`.

### Companion

- official/custom Caddy image chosen by the operator;
- `ghcr.io/juloc/caddy-ui-companion:<version>` as the smaller companion.

Both modes use exactly two containers. Caddy initialization is idempotent and performed by Caddy UI before management becomes writable. DDNS and observability jobs run as supervised background threads inside Caddy UI. No Docker socket is mounted.

## Release flow

1. Merge to `main`.
2. Determine bump: `major` or `minor` PR label; otherwise `patch`.
3. Create SemVer tag and GitHub Release.
4. Publish immutable version tags and `latest` for stable releases.
5. Verify the bundle and companion images.
6. Open an automated PR in `Juloc/docker` updating all Caddy image references in `caddy/docker-compose.yml`.
7. Auto-merge only after repository checks pass.

Pre-releases publish `1.0.0-alpha.N`/`beta.N` tags but do not replace stable `latest` unless explicitly promoted.
