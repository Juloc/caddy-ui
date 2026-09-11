# Caddy UI code, architecture and UX audit

Status: active  
Baseline: main / 2.1.20  
Audit date: 2026-09-08  
Last updated: 2026-09-11

This document records concrete problems found during the full repository audit. It is the problem ledger; implementation order and progress are tracked in `docs/IMPROVEMENT_PLAN.md`.

## P1 - Route state and save semantics

### Draft state is presented as active Caddy state

Route changes are persisted to PostgreSQL before they are applied to Caddy. The route overview currently renders labels such as `Aktiv` and `Deaktiviert` from the persisted route definition, even when the active Caddy revision still differs.

Consequences:

- a newly saved route can look active before Apply;
- a disabled route can look inactive while Caddy still serves it;
- a deleted route can disappear from the UI while the applied configuration still contains it;
- operators cannot reliably tell whether the UI reflects desired state or active runtime state.

Tracking: #72

### Quick route creation has no Save-only action

The simple route dialog currently offers only `Erstellen & aktivieren`. The advanced route editor supports both Save and Save+Apply.

The simple flow must support persisting a route without forcing immediate activation, and the result must be clearly presented as saved but not yet applied.

Tracking: #71

## P2 - Persistence and service structure

Several infrastructure classes have accumulated too many responsibilities:

- `AnalyticsIngestionStore.cs` ~54 KB
- `OperationsStore.cs` ~45 KB
- `RouteManagementStore.cs` ~44 KB
- `AnalyticsReadStore.cs` ~40 KB

`RouteManagementStore` currently contains route persistence, access groups, credentials, revisions, snapshots and apply-operation persistence. These responsibilities should be separated behind focused services/repositories without introducing compatibility copies.

EF Core is used for schema/migrations and connection creation while most runtime persistence uses handwritten `DbCommand` SQL. The current approach is valid but causes repeated connection, transaction, parameter and reader code. Shared low-level helpers should be introduced only where they reduce duplication without hiding SQL behavior.

## P2 - UI and UX consistency

### Design contract and implemented route UI have drifted

`docs/UI_DESIGN_CONTRACT.md` still mandates route tables and desktop create/edit dialogs. The current domain-first route UI deliberately uses grouped route rows and the advanced editor is a dedicated page.

The contract must describe the actual intended product, not an earlier UI.

### Simple and advanced route workflows are inconsistent

Actions, wording and state feedback differ between the quick-create dialog and advanced route editor. Save, Apply and pending-change semantics must be consistent across both paths.

### Applied/pending changes are not first-class UI states

The UI needs explicit states such as:

- Applied
- Changed / Apply required
- New draft
- Pending removal
- Apply failed

The active runtime state must never be inferred only from the desired route record.

## P2 - Localization consistency

Localization rules currently conflict:

- `docs/MULTILINGUAL_UI.md` says English-first and default `en`;
- runtime configuration and tests use German as the default;
- `AGENTS.md` says product UI text is German;
- several newer Razor pages contain directly hard-coded German text instead of localization resources;
- the user-settings page normalizes a missing stored preference through the technical English fallback, so it can preselect `en` even though the request itself correctly renders with configured default culture `de`.

One canonical language policy must be chosen and enforced. Product UI should use localization resources where multilingual support is intended, and a missing/unsupported user preference must resolve to the configured default culture rather than the source-key fallback.

Tracking: #77

## P2 - Documentation drift

There are two architecture documents:

- `docs/ARCHITECTURE.md`
- `docs/architecture.md`

They are substantially duplicated and have already diverged.

`docs/BACKLOG.md` is also stale: it still contains Caddy UI 1.x, Python and SQLite implementation history although the supported runtime is .NET 10 and PostgreSQL.

Documentation should have one canonical architecture source and one current backlog/plan.

## P2 - Test quality

The repository has broad test coverage by project, but several web tests assert source-code strings in Razor/CSS files instead of exercising behavior.

Critical workflows that need real integration coverage include:

- quick route Save-only;
- quick route Save+Apply;
- advanced Save;
- advanced Save+Apply;
- enable/disable with unapplied changes;
- delete with pending removal;
- failed Apply and visible state;
- successful Apply and state convergence.

There is currently no enforced code-coverage threshold in CI.

## P3 - CI duplication

`.github/workflows/dotnet.yml` and `.github/workflows/release.yml` repeat restore, formatting, build and test stages. Shared/reusable workflow steps should reduce drift and unnecessary maintenance.

## P3 - Repository hygiene

Current branch/PR state from the audit:

- `fix/domain-wizard-ddns` / PR #70: relevant active work;
- `agent/acme-email-ui` / PR #39: heavily behind main and needs re-evaluation;
- `agent/acme-email-ui-rebased`: stale;
- `agent/domain-first-routing-ui`: PR #68 already merged;
- `release/2.1.20`: no unique work remaining.

Stale merged/replaced branches should be removed after confirming no unmerged work is required.

## Positive findings

The audit also confirmed several strong foundations:

- clean project dependency direction: Domain / Application / Infrastructure / Web;
- deterministic route compilation;
- validation before Caddy activation;
- atomic managed-fragment replacement;
- rollback path after failed Apply;
- PostgreSQL as the runtime source of truth;
- no Docker socket in the UI container;
- authorization and anti-forgery protection on state-changing UI flows;
- dedicated Go modules for the Caddy guard and Netcup provider;
- successful main CI for version 2.1.20.

These foundations should be preserved while addressing the issues above.
