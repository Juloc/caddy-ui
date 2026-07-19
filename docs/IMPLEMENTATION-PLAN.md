# Implementation Plan

## Ordering rationale

The implementation begins with persistence, migration, security, and configuration transactions because every visible feature depends on these contracts. The application shell follows only after controllers and services have stable boundaries. Operational charts and automation come last because they consume the established data and release models.

## Execution order

1. Baseline tests around current routes, providers, Netcup behavior, and rollback.
2. New modular package, SQLite schema, repositories, backup, and migration.
3. Identity, roles, sessions, CSRF, TOTP, and audit.
4. Managed/custom route domain model and transactional Caddy apply pipeline.
5. Access groups and portal authentication.
6. Fluent 2 shell, navigation, themes, dialogs, and shared components.
7. Pages in product priority order: Routes, Dashboard, Logs, System, DNS, Access, Administration.
8. Monitoring, aggregation, notification, retention, and backup jobs.
9. Two-container deployment and companion/bundle images.
10. SemVer release and downstream Docker PR workflow.
11. Full verification, migration rehearsal, documentation update, and dead-code removal.

## Completion rule

A checkbox in `docs/BACKLOG.md` is complete only when implementation, automated verification, relevant manual/visual verification, documentation, and cleanup are all complete. Partial scaffolding does not count.

## Current state

Steps 1 through 10 are implemented. Local verification evidence is recorded in `VERIFICATION.md`. Step 11 remains active until the GitHub pull-request checks and deployed desktop/mobile smoke checks have passed.
