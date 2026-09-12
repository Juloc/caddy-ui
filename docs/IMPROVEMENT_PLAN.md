# Caddy UI improvement plan

Status: active  
Baseline: main / 2.1.20  
Last updated: 2026-09-12

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

- [x] **DONE** Add a Save-only action to the quick route dialog.
- [x] **DONE** Keep `Erstellen & aktivieren` as the explicit Save+Apply action.
- [x] **DONE** Ensure Save-only does not create/apply a Caddy revision.
- [x] **DONE** Use consistent wording and success/error states with the advanced editor.
- [x] **DONE** Add regression coverage for both actions, including PostgreSQL lifecycle verification that Save-only creates no revision/apply operation.

Verification evidence for Phase 2: PR #74, GitHub Actions **Verify .NET application** run #212 — restore, formatting, Release build, all .NET tests, production image builds, PostgreSQL/Caddy UI startup, authenticated page smoke tests, SQLite migration CLI and bundled Caddy module verification all passed.

## Phase 3 - UI/UX contract cleanup

Tracking: #75

- [x] **DONE** Decide and document the canonical domain-first route layout.
- [x] **DONE** Update `docs/UI_DESIGN_CONTRACT.md` and `AGENTS.md` where they conflicted with the intended current UI.
- [x] **DONE** Audit route, domain, DNS, access and system pages for primary/secondary action consistency.
- [x] **DONE** Audit desktop, tablet and mobile layouts against the documented breakpoints.
- [x] **DONE** Verify keyboard flow, focus return, 200% text scaling and 400%-equivalent 320 px reflow in Chromium acceptance.
- [x] **DONE** Verify Light, Dark and System themes in Chromium acceptance.
- [x] **DONE** Remove inconsistent primary styling from repeated Provider Test, DNS Synchronize and DDNS Run-now row actions.

Verification evidence for Phase 3: PR #76, GitHub Actions **Verify .NET application** run #218 passed restore, formatting, Release build, all .NET/PostgreSQL tests, production image builds, PostgreSQL/Caddy UI startup, authenticated page smoke tests, Chromium UI/UX browser acceptance, SQLite migration CLI and bundled Caddy module verification. Run #215 independently passed the same implementation-level browser acceptance before the final documentation updates.

## Phase 4 - Localization cleanup

Tracking: #77

- [x] **DONE** Choose one canonical default-language policy: German (`de`) UI default, English source-key fallback, supported UI cultures `de` and `en`.
- [x] **DONE** Reconcile `MULTILINGUAL_UI.md`, `AGENTS.md`, runtime config, persistence defaults and tests.
- [x] **DONE** Move hard-coded product UI strings on the audited routing/settings surfaces into the localization resource path.
- [x] **DONE** Add regression rules/tests preventing new localization drift and verify default/preferred culture behavior.

Verification evidence for Phase 4: PR #78, GitHub Actions **Verify .NET application** run #237 — restore, formatting, Release build, all .NET tests, production image builds, PostgreSQL/Caddy UI startup, locale-neutral authenticated page smoke, Chromium default German plus `de → en → de` preference/rendering acceptance, SQLite migration CLI and bundled Caddy module verification all passed.

## Phase 5 - Infrastructure decomposition

No large rewrite. Split by responsibility while preserving behavior and tests.

### Route persistence

- [x] **DONE** Split route CRUD from access-group/credential persistence.
- [x] **DONE** Split revision/snapshot/apply-operation persistence from route CRUD.
- [x] **DONE** Remove duplicated transaction/command boilerplate where a focused helper improves clarity. Tracking: #81

Verification evidence for the access-persistence slice: PR #79, GitHub Actions **Verify .NET application** run #244 — restore, formatting, Release build, all .NET/PostgreSQL tests including the access-group/credential lifecycle and ownership boundary, production image builds, PostgreSQL/Caddy UI startup, authenticated/browser acceptance, SQLite migration CLI and bundled Caddy module verification all passed. Final documentation head was re-verified by run #245; its initial mobile-focus browser attempt was flaky and the unchanged-head retry passed the complete acceptance suite.

Verification evidence for the route-apply persistence slice: PR #80, GitHub Actions **Verify .NET application** run #249 passed the implementation head. Final documentation head run #250 and post-merge main run #251 passed restore, formatting, Release build, all .NET/PostgreSQL tests including the route/apply ownership boundary and apply lifecycle, production image builds, PostgreSQL/Caddy UI startup, authenticated page smoke, stabilized Chromium focus/UI/UX/localization acceptance, SQLite migration CLI and bundled Caddy module verification. The browser contract waits for the asynchronous mobile-navigation focus transfer and focus return within a bounded interval instead of sampling the same event tick; the accessibility requirement itself is unchanged.

Verification evidence for the route-persistence plumbing slice: PR #82, GitHub Actions **Verify .NET application** runs #252 and #253 plus post-merge main run #254 — restore, formatting, Release build, all .NET/PostgreSQL tests including the plumbing ownership guard and existing route/apply lifecycle, production image builds, PostgreSQL/Caddy UI startup, authenticated page smoke, Chromium UI/UX/localization acceptance, SQLite migration CLI and bundled Caddy module verification all passed. `RelationalStoreSupport` owns only EF-backed connection opening, parameter binding and simple non-query execution; domain SQL, audit semantics and transaction boundaries remain explicit in the owning stores.

### Operations

- [ ] **IN PROGRESS** Split DNS provider runtime state, managed DNS records and DDNS persistence out of `OperationsStore` into `DnsOperationsStore`. Tracking: #83
- [ ] **TODO** Split notification persistence out of `OperationsStore`.
- [ ] **TODO** Split scheduled-job persistence out of `OperationsStore`.
- [ ] **TODO** Split health-check persistence out of `OperationsStore`.
- [ ] **TODO** Split backup persistence out of `OperationsStore`.
- [ ] **TODO** Keep transaction boundaries explicit across all focused operations stores.

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
