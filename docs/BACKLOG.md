# Caddy UI 1.0 Backlog

This file is the persistent implementation ledger. Update status and verification evidence as work progresses.

## Phase 0 - Baseline and documentation

- [x] Capture product requirements.
- [x] Define target architecture, security model, UX rules, and release flow.
- [x] Identify current monolith and obsolete App Templates scope.
- [x] Record legacy migration and route behavior with regression fixtures.

## Phase 1 - Foundation and migration

- [x] Create modular Python package and application entrypoint.
- [x] Add SQLite connection, schema migrations, transactions, and health checks.
- [x] Add automatic pre-migration backups and rollback.
- [x] Import existing JSON provider settings and managed route metadata.
- [x] Remove App Templates and all related code/routes/docs.
- [x] Add typed configuration and structured logging.

## Phase 2 - Identity, authorization, and audit

- [x] Bootstrap first administrator safely.
- [x] Implement Administrator, Editor, and Viewer permissions.
- [x] Implement strong password hashing, server-side sessions, CSRF, logout, and rate limiting.
- [x] Add optional TOTP.
- [x] Implement user administration.
- [x] Implement append-only, redacted, indefinite audit logging.

## Phase 3 - Caddy configuration core

- [x] Implement managed route model for paths, headers, upstreams, load balancing, health checks, redirects, TLS, and access groups.
- [x] Implement deterministic rendering and complete-config validation.
- [x] Add preview/diff/apply/reload/verify/rollback workflow.
- [x] Add immutable configuration revisions and restore.
- [x] Add enable/disable, duplicate, bulk actions, import, and export.
- [x] Add administrator-only Custom Routes and safe unmanaged-snippet import.

## Phase 4 - Access portal

- [x] Add reusable access groups and credentials.
- [x] Add branded form-login portal with secure cookies.
- [x] Add per-group name, logo, text, and accent.
- [x] Reserve inactive provider extension points for forward-auth and OIDC.

## Phase 5 - Fluent 2 application shell

- [x] Implement compact desktop shell and mobile drawer.
- [x] Implement System/Light/Dark three-state theme.
- [x] Add Fluent icons, tokens, accessible controls, and responsive dialogs.
- [x] Implement Dashboard, Routes, Access, Logs, System, DNS, and Administration navigation.
- [x] Remove old embedded CSS and duplicate page rendering.

## Phase 6 - Operational areas

- [x] Dashboard problem-first layout, traffic charts, and inventory.
- [x] Route table with configurable columns and dual health status.
- [x] Log tabs with live/pause/search/filter/download.
- [x] System status, certificates, validate/reload/diagnostics/revisions/restore.
- [x] DNS and Netcup DDNS integration through provider adapter.
- [x] Dashboard/email/webhook notifications.
- [x] Detailed 30-day, daily older, monthly one-year traffic retention.
- [x] Daily backups and restore UI.

## Phase 7 - Deployment and release automation

- [x] Reduce Compose deployment to `caddy` and `caddy-ui` containers.
- [x] Publish definitions for companion UI and Netcup Caddy bundle images.
- [x] Add health checks and idempotent initialization.
- [x] Add SemVer label-based release automation; patch is default.
- [x] Add alpha/beta channel before `1.0.0`.
- [x] Open verified automated version PRs against `Juloc/docker/caddy/docker-compose.yml`.

## Phase 8 - Verification and cleanup

- [x] Python unit/integration tests.
- [x] Go tests, formatting, and committed module graph on Go 1.25.1.
- [x] Caddy rendering, failure, and rollback tests; live validation runs in container CI.
- [x] Migration and restore tests using current production-format fixtures.
- [x] Role/permission, CSRF, session, password, TOTP, and redaction tests.
- [ ] Desktop/mobile, light/dark, keyboard, accessibility, and visual checks.
- [x] Companion and bundled Caddy container builds.
- [ ] Deployed two-container smoke test.
- [x] Remove dead code, unused configuration, obsolete docs, and duplicated behavior.
- [ ] Confirm every product requirement has implementation and verification evidence.
