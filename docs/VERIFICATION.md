# Verification Record

## Local verification

Completed on 2026-07-19:

- Python bytecode compilation for the application, entrypoint, scripts, and tests.
- 23 unit and HTTP integration tests covering authentication, roles, CSRF, password hashing, TOTP, audit redaction, SQLite backup/restore, schema migration, legacy Caddy migration, route rendering, preview isolation, apply rollback, revision rollback, traffic retention, health notification deduplication, release versioning, and the two-service deployment contract.
- JavaScript syntax validation with `node --check`.
- Git whitespace/error validation with `git diff --check`.
- YAML parsing for both GitHub Actions workflows.
- SemVer promotion rehearsal from alpha to beta.

## CI verification

GitHub Actions run 9 completed successfully on 2026-07-19 for pull request #1:

- Python compilation and all 23 unit/integration tests.
- Go formatting, committed module graph, and `go test ./...` using Go 1.25.1.
- Companion container image build.
- Bundle container image build including Caddy 2.11.4 and the Netcup module.

## Manual acceptance checklist

- Desktop: verify the compact navigation, flat workspaces, dashboard cards, table overflow, and dialogs.
- Mobile: verify the drawer and full-screen dialogs at widths up to 900 px.
- Themes: verify System, Light, and Dark modes plus the configured accent.
- Accessibility: verify keyboard navigation, visible focus, labels, dialog close controls, reduced motion, and useful status text.
- Deployment: start `compose.yml`, confirm exactly `caddy` and `caddy-ui`, create a route, validate/reload it, and exercise rollback with an intentionally invalid administrator-only Custom Route.

The remaining checks stay open in `docs/BACKLOG.md` until the deployed visual, accessibility, and two-container smoke passes complete.
