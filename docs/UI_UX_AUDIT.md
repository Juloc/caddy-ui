# Caddy UI UI/UX audit

Status: active remediation record  
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

Finding: row-level `Test` is visually primary even though `Add provider` is already the page primary action. Row-level diagnostic actions should be secondary. The `Save timing` submit remains primary inside its explicitly opened local form.

### Access

Status: consistent.

- `Create group` is the page primary action;
- `Create credential` is secondary;
- row edit/enable/disable are secondary;
- delete is danger-styled and guarded.

### DNS / DDNS operations

Finding: `Synchronize` and `Run now` are visually primary in every table row. These are row-local operational actions and should be secondary. `Create record` and `Create DDNS target` remain primary inside their independent creation workbenches.

### Production readiness / system operations

Status: consistent.

- `Check readiness now` is the dominant page action;
- statistics comparison is secondary;
- status information is not presented as an action.

## Dialog and editor contract

Short, self-contained create/edit tasks use the shared native `<dialog>` pattern.

The advanced route editor is intentionally a dedicated page because it contains multiple sections, conditional route-type panels, advanced disclosures and a persistent save/apply action bar. Forcing that workflow into a desktop modal would reduce available space and make navigation/history less clear.

On screens below 640 px, shared dialogs become full-screen. This keeps the same task and form semantics while avoiding cramped nested scrolling.

## Keyboard and focus review

The shared `dialogs.js` implementation:

- stores the opener element;
- focuses the first meaningful interactive control after opening;
- uses native `showModal()` where supported, which provides modal focus containment;
- returns focus to the original opener on close;
- preserves the submitting button through confirmation dialogs.

The route advanced-disclosure script updates `aria-expanded` and moves focus into a panel when it is opened.

Remaining acceptance work: visual/manual or browser-automation verification at 200% text zoom and 400% page zoom. Static CSS review alone is not recorded as proof of those two zoom requirements.

## Responsive layout review

Current implementation breakpoints are intentional and should remain the canonical CSS implementation points unless a feature has a documented local need:

- `<= 1024 px`: desktop sidebar becomes an overlay/off-canvas navigation;
- `<= 760 px`: workspaces/forms become one column and page/action layouts stack;
- `<= 639 px`: shared dialogs become full-screen;
- `<= 420 px`: sidebar footer controls stack;
- coarse pointer: interactive controls use at least 44 px target height.

The domain-first routing layout also collapses its row grid at 1180 px and becomes a single-column route row at 760 px.

## Theme, contrast and motion review

The CSS has explicit Light, Dark and System paths using the same semantic `--ui-*` aliases. System follows `prefers-color-scheme`.

The implementation also contains:

- global `:focus-visible` treatment;
- `prefers-reduced-motion` handling;
- `prefers-contrast: more` handling;
- `forced-colors: active` handling;
- semantic status colors paired with text labels;
- no dependency on color alone for route runtime state.

Remaining acceptance work: visually verify representative Routing, Domains, Providers, Access and Operations pages in System, Light and Dark before Phase 3 is marked fully complete.

## Changes required by this audit

- update `docs/UI_DESIGN_CONTRACT.md` to allow hierarchical semantic lists for domain-first routing;
- update the dialog rule so complex multi-section editors may use dedicated pages;
- align documented breakpoints with the implemented 1024/760/639/420 behavior;
- define primary actions per action scope, not once for an entire page regardless of independent workbenches;
- demote repeated provider/DNS row diagnostic actions from primary styling;
- keep zoom/theme checks explicit until they have real acceptance evidence.
