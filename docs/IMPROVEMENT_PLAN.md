# Caddy UI improvement plan

Status: active  
Baseline: main / 2.1.20  
Last updated: 2026-09-08

This is the living implementation plan for the repository audit in `docs/CODE_AUDIT.md`.

## Maintenance rule

This file must be updated in the same workstream whenever an audit item is started, materially changed, completed, replaced or found to be unnecessary.

Do not mark an item complete only because code was written. Completion requires the relevant verification/tests and documentation updates.

Status values:

- `TODO` — not started
- `IN PROGRESS` — active implementation
- `BLOCKED` — cannot currently proceed
- `DONE` — implemented and verified
- `CANCELLED` — intentionally not implemented, with reason recorded

## Phase 1 - Correct route state model

Highest priority because the current UI can present desired database state as if it were already active in Caddy.

- [x] **DONE** Define an explicit desired-vs-applied route state model. Tracking: #72
- [x] **DONE** Derive the active revision from the actual managed-fragment digest, including rollback-safe matching.
- [x] **DONE** Show `Applied`, `Apply required`, `New draft`, `Pending removal`, `Disabled`, `Unknown` and failed-apply states.
- [x] **DONE** Keep deleted-but-still-applied routes visible until Apply removes them.
- [x] **DONE** Align enable/disable/delete feedback with actual applied state.
- [x] **DONE** Add PostgreSQL + Apply lifecycle integration tests for save/toggle/delete/apply convergence and failed Apply.
- [x] **DONE** Update route-state documentation. See `docs/ROUTE_STATE_MODEL.md`.

Verification evidence for Phase 1: PR #73, GitHub Actions **Verify .NET application** run #208 — restore, formatting, Release build, all .NET tests, production image builds, PostgreSQL/Caddy UI startup, authenticated page smoke tests including `/Routing`, SQLite migration CLI and bundled Caddy module verification all passed.

## Phase 2 - Fix simple route creation

Tracking: #71

- [ ] **TODO** Add a Save-only action to the quick route dialog.
- [ ] **TODO** Keep `Erstellen & aktivieren` as the explicit Save+Apply action.
- [ ] **TODO** Ensure Save-only does not create/apply a Caddy revision.
- [ ] **TODO** Use consistent wording and success/error states with the advanced editor.
- [ ] **TODO** Add behavioral regression tests for both actions.

## Phase 3 - UI/UX contract cleanup

- [ ] **TODO** Decide and document the canonical domain-first route layout.
- [ ] **TODO** Update `docs/UI_DESIGN_CONTRACT.md` where it conflicts with the intended current UI.
- [ ] **TODO** Audit route, domain, DNS, access and system pages for primary/secondary action consistency.
- [ ] **TODO** Audit desktop, tablet and mobile layouts against the documented breakpoints.
- [ ] **TODO** Verify keyboard flow, focus return, 200% text zoom and 400% page zoom.
- [ ] **TODO** Verify Light, Dark and System themes.
- [ ] **TODO** Remove redundant actions and inconsistent labels.

## Phase 4 - Localization cleanup

- [ ] **TODO** Choose one canonical default-language policy.
- [ ] **TODO** Reconcile `MULTILINGUAL_UI.md`, `AGENTS.md`, runtime config and tests.
- [ ] **TODO** Move hard-coded product UI strings into the localization resource path where required.
- [ ] **TODO** Add a regression rule/test preventing new localization drift.

## Phase 5 - Infrastructure decomposition

No large rewrite. Split by responsibility while preserving behavior and tests.

### Route persistence

- [ ] **TODO** Split route CRUD from access-group/credential persistence.
- [ ] **TODO** Split revision/snapshot/apply-operation persistence from route CRUD.
- [ ] **TODO** Remove duplicated transaction/command boilerplate where a focused helper improves clarity.

### Operations

- [ ] **TODO** Split DNS/DDNS, notifications, jobs, health checks and backups out of `OperationsStore`.
- [ ] **TODO** Keep transaction boundaries explicit.

### Analytics

- [ ] **TODO** Split ingestion responsibilities into smaller focused persistence components.
- [ ] **TODO** Split analytics read/query groups where this reduces file and responsibility size.
- [ ] **TODO** Preserve current ingestion performance and retention behavior with tests.

## Phase 6 - Test architecture

- [ ] **TODO** Replace critical source-string assertions with behavioral/integration tests.
- [ ] **TODO** Keep source-contract tests only for rules that are genuinely static repository contracts.
- [ ] **TODO** Add route state/apply integration coverage.
- [ ] **TODO** Add measurable code-coverage reporting.
- [ ] **TODO** Decide a realistic CI coverage floor after obtaining a baseline.

## Phase 7 - Documentation cleanup

- [ ] **TODO** Merge `docs/ARCHITECTURE.md` and `docs/architecture.md` into one canonical document.
- [ ] **TODO** Remove or archive stale 1.x/Python/SQLite backlog material.
- [ ] **TODO** Make this improvement plan the current audit execution ledger.
- [ ] **TODO** Verify README, architecture, deployment and UI contracts describe the same runtime.

## Phase 8 - CI and repository hygiene

- [ ] **TODO** Reduce duplicated .NET verification between CI and release workflows.
- [ ] **TODO** Review PR #70 and integrate when ready.
- [ ] **TODO** Re-evaluate or close stale PR #39.
- [ ] **TODO** Remove merged/replaced stale branches after verifying no unique work remains.
- [ ] **TODO** Keep release branches only while they serve an active release purpose.

## Completion criteria

The audit remediation is complete when:

1. desired and applied route state cannot be confused in the UI;
2. both simple and advanced route creation support clear Save and Save+Apply semantics;
3. critical route flows have behavioral integration coverage;
4. documentation has no conflicting architecture/language/UI contracts;
5. large stores have focused responsibilities without behavior regressions;
6. desktop/mobile/theme/accessibility verification is recorded;
7. CI and repository hygiene no longer contain known audit debt;
8. this plan contains no unresolved `TODO`, `IN PROGRESS` or `BLOCKED` items.
