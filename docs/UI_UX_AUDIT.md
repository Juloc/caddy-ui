# Caddy UI UI/UX audit

Status: completed  
Date: 2026-09-10  
Tracking: #75

## Scope

This audit reconciles the current Razor UI with `docs/UI_DESIGN_CONTRACT.md`. It covers Routing, Domains, DNS providers, Access, Operations/Cutover, shared dialogs, responsive CSS, themes, touch targets and focus behavior.

## Canonical route-management layout

The route overview is intentionally **domain-first** rather than a flat route table.

Canonical structure:

1. one page-level route overview;
2. routes grouped under their managed domain;
3. each domain group shows domain state, route count and one clear `+ Route` action;
4. each route is one semantic list row with address, target, access/runtime state and actions;
5. uncommon route actions stay in the row overflow menu;
6. deleted-but-still-active routes remain visibly represented until Apply removes them;
7. quick creation is a dialog; the advanced multi-section route editor is a dedicated page.

This is an intentional exception to the default table pattern. A table remains the standard for flat datasets such as requests, users, revisions, DNS records and provider inventories.

## Action hierarchy audit

### Routing

Status: consistent after #71/#72.

- page-level navigation and preview actions are secondary;
- `+ Route` is the dominant creation action inside a domain group;
- quick-create offers secondary `Speichern` and primary `Erstellen & aktivieren`;
- advanced editor offers secondary `Save` and primary `Save and activate/update`;
- row edit/open actions are secondary and destructive actions are separated.

### Domains

Status: consistent.

- `Add domain` is the page primary action;
- `Refresh status` and guided setup are secondary;
- per-domain state changes are secondary;
- certificate retry is intentionally secondary because it is a diagnostic/recovery action.

### DNS providers

Status: corrected in #76.

- `Add provider` remains the page primary action;
- repeated row-level `Test` is now secondary;
- `Save timing` remains primary inside its explicitly opened local form.

### Access

Status: consistent.

- `Create group` is the page primary action;
- `Create credential` is secondary;
- row edit/enable/disable are secondary;
- delete is danger-styled and guarded.

### DNS / DDNS operations

Status: corrected in #76.

- repeated `Synchronize` and `Run now` row operations are secondary;
- `Create record` and `Create DDNS target` remain primary inside their independent creation workbenches.

### Production readiness / system operations

Status: consistent.

- `Check readiness now` is the dominant page action;
- statistics comparison is secondary;
- status information is not presented as an action.

## Dialog and editor contract

Short, self-contained create/edit tasks use the shared native `<dialog>` pattern.

The advanced route editor is intentionally a dedicated page because it contains multiple sections, conditional route-type panels, advanced disclosures and a persistent save/apply action bar. Forcing that workflow into a desktop modal would reduce available space and make navigation/history less clear.

On screens below 640 px, shared dialogs become full-screen. This keeps the same task and form semantics while avoiding cramped nested scrolling.

`AGENTS.md` and `docs/UI_DESIGN_CONTRACT.md` now describe the same rule so future work does not reintroduce the old all-desktop-editors-are-dialogs constraint.

## Keyboard and focus review

The shared `dialogs.js` implementation:

- stores the opener element;
- focuses the first meaningful interactive control after opening;
- uses native `showModal()` where supported, which provides modal focus containment;
- returns focus to the original opener on close;
- preserves the submitting button through confirmation dialogs.

The route advanced-disclosure script updates `aria-expanded` and moves focus into a panel when it is opened.

The mobile navigation:

- makes background application content inert while open;
- exposes the sidebar as a modal dialog;
- traps Tab/Shift+Tab inside the drawer;
- closes on Escape;
- returns focus to the opening control.

Browser acceptance in GitHub Actions run #218 verified dialog focus transfer/return and mobile drawer focus/Escape behavior in Chromium.

## Responsive layout review

Current implementation breakpoints are intentional and remain the canonical CSS implementation points unless a feature has a documented local need:

- `<= 1024 px`: desktop sidebar becomes an overlay/off-canvas navigation;
- `<= 760 px`: workspaces/forms become one column and page/action layouts stack;
- `<= 639 px`: shared dialogs become full-screen;
- `<= 420 px`: sidebar footer controls stack;
- coarse pointer: interactive controls use at least 44 px target height.

The domain-first routing layout also collapses its row grid at 1180 px and becomes a single-column route row at 760 px.

Browser acceptance in run #218 loaded representative Routing, Domains, Providers, Access, DNS/DDNS and Cutover pages at a 320 px CSS viewport, the reflow target corresponding to a 1280 px page viewed at 400% zoom, and rejected page-level horizontal overflow. It also applied a deterministic 200% computed-font scaling stress check to the main management pages and rejected page-level horizontal overflow.

## Theme, contrast and motion review

The CSS has explicit Light, Dark and System paths using the same semantic `--ui-*` aliases. System follows `prefers-color-scheme`.

The implementation also contains:

- global `:focus-visible` treatment;
- `prefers-reduced-motion` handling;
- `prefers-contrast: more` handling;
- `forced-colors: active` handling;
- semantic status colors paired with text labels;
- no dependency on color alone for route runtime state.

Browser acceptance in run #218 verified that Light and Dark produce distinct computed foreground/background colors, that the selected theme exposes the correct `aria-pressed` state, and that System follows both simulated light and dark `prefers-color-scheme` values.

## Regression coverage

Phase 3 adds two layers of regression protection:

1. `UiUxContractTests` locks the semantic domain-first list, documented breakpoints, theme/accessibility rules, focus contracts and row-action hierarchy.
2. `tests/browser/ui-ux-smoke.cjs` exercises the built application in Chromium after real login against PostgreSQL-backed Caddy UI.

The browser smoke is part of the standard acceptance workflow and changes under `tests/browser/**` trigger the workflow.

## Verification evidence

GitHub Actions **Verify .NET application** run #218 passed on the final Phase-3 implementation and documentation state before the evidence-only commit:

- restore and formatting verification;
- Release build;
- all .NET/PostgreSQL tests;
- production image builds;
- PostgreSQL and Caddy UI startup;
- authenticated page smoke tests;
- Chromium UI/UX browser acceptance;
- SQLite migration CLI;
- bundled Caddy module verification.

Run #215 independently passed the same implementation-level browser acceptance before the final documentation updates.
