# Verification Record

## Local verification

Completed on 2026-07-19:

- Python bytecode compilation for the application, entrypoint, scripts, and tests.
- 23 unit and HTTP integration tests covering authentication, roles, CSRF, password hashing, TOTP, audit redaction, SQLite backup/restore, schema migration, legacy Caddy migration, route rendering, preview isolation, apply rollback, revision rollback, traffic retention, health notification deduplication, release versioning, and the two-service deployment contract.
- JavaScript syntax validation with `node --check`.
- Git whitespace/error validation with `git diff --check`.
- YAML parsing for both GitHub Actions workflows.
- SemVer promotion rehearsal from alpha to beta.

## CI verification required

The pull-request workflow is configured to complete the checks unavailable in the local environment:

- Go formatting and `go test ./...` using the version declared in `go.mod`.
- Companion container build.
- Bundle container build including Caddy 2.11.4 and the Netcup module.
- Caddyfile adaptation as part of the bundle build/smoke path.

## Manual acceptance checklist

- Desktop: verify the compact navigation, flat workspaces, dashboard cards, table overflow, and dialogs.
- Mobile: verify the drawer and full-screen dialogs at widths up to 900 px.
- Themes: verify System, Light, and Dark modes plus the configured accent.
- Accessibility: verify keyboard navigation, visible focus, labels, dialog close controls, reduced motion, and useful status text.
- Deployment: start `compose.yml`, confirm exactly `caddy` and `caddy-ui`, create a route, validate/reload it, and exercise rollback with an intentionally invalid administrator-only Custom Route.

The final three checks stay open in `docs/BACKLOG.md` until GitHub CI and the deployed visual/smoke pass complete.
