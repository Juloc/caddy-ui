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
          -> log/stat collectors
          -> notification adapters
  -> Caddy reverse proxy
```

The application remains a server-rendered Python service with a small amount of dependency-free JavaScript. Go remains limited to the custom Caddy build and Netcup DNS module.

## Module boundaries

- `caddy_ui/config.py`: environment and path settings.
- `caddy_ui/db.py`: SQLite connection, migrations, transactions, backup primitives.
- `caddy_ui/domain.py`: typed domain objects and validation.
- `caddy_ui/repositories.py`: persistence access.
- `caddy_ui/security.py`: password hashing, sessions, CSRF, TOTP, permissions.
- `caddy_ui/audit.py`: append-only redacted audit events.
- `caddy_ui/caddy.py`: route rendering, import, validation, reload, revisions, rollback.
- `caddy_ui/providers/`: provider contracts and Netcup implementation.
- `caddy_ui/monitoring.py`: health probes, certificates, log parsing, aggregation.
- `caddy_ui/notifications.py`: dashboard, email, and webhook dispatch.
- `caddy_ui/db.py`: scheduled and pre-migration backup/restore primitives.
- `caddy_ui/web.py`, `views.py`, and `static/`: request routing, templates, and assets.
- `caddy_ui/jobs.py`: DDNS, aggregation, retention, health checks, and backup schedules.

Presentation code must not call SQLite, provider APIs, or Caddy directly.

## Configuration ownership

- Managed routes are stored in SQLite as the source of truth and rendered deterministically to individual snippets.
- Custom routes are stored separately and are administrator-only.
- Unmanaged files are never silently changed.
- Every applied configuration has an immutable revision manifest and content digest.
- Secrets are excluded from diffs, audit payloads, exports, and diagnostic bundles.

## Deployment

### Bundle

- `ghcr.io/juloc/caddy-ui:<version>` contains both runtimes and is started once as `caddy` and once as `caddy-ui`.

### Companion

- official/custom Caddy image chosen by the operator;
- `ghcr.io/juloc/caddy-ui-companion:<version>` as the smaller companion.

Both modes use two containers. Caddy initialization is idempotent and performed by Caddy UI before management becomes writable. DDNS runs as a supervised background job inside Caddy UI.

## Release flow

1. Merge to `main`.
2. Determine bump: `major` or `minor` PR label; otherwise `patch`.
3. Create SemVer tag and GitHub Release.
4. Publish immutable version tags and `latest` for stable releases.
5. Verify the bundle and companion images.
6. Open an automated PR in `Juloc/docker` updating all Caddy image references in `caddy/docker-compose.yml`.
7. Auto-merge only after repository checks pass.

Pre-releases publish `1.0.0-alpha.N`/`beta.N` tags but do not replace stable `latest` unless explicitly promoted.
