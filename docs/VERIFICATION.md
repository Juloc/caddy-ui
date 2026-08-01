# Verification Record

## Mandatory Fluent 2 Web verification matrix

Apply this matrix to every UI-affecting work item before it is complete. It supplements the build, migration, Caddy reload, and container checks below; a passing visual check never replaces the operational checks.

| Area | Required check | Acceptance condition |
| --- | --- | --- |
| Rendering baseline | Disable nonessential JavaScript and exercise page navigation, forms and state-changing postbacks | Razor HTML, CSRF, validation and browser navigation remain functional |
| Semantic structure | Inspect landmarks, headings, links, buttons, form labels and tables | Native elements describe the actual interaction; no ARIA compensates for incorrect semantics |
| Tokens and visual language | Review light and dark rendering plus computed styles | Only central `--ui-*` aliases are used; surfaces, strokes, focus, status and elevation are distinguishable |
| Themes | Test System, Light and Dark, including a system scheme change | System follows `prefers-color-scheme`; explicit mode overrides it; native controls follow `color-scheme` |
| Keyboard and focus | Tab through the shell, controls, overlays and dialogs | Focus is visible, ordered, trapped only by modal dialogs, and restored to the invoking element |
| Dialog and drawer | Test open, Escape, safe exit, submit/cancel and destructive confirmation | No nested dialogs; desktop editing is a dialog and small-screen editing is full-screen |
| Responsive layout | Test 320px, 480px, 640px, 760px, 1024px and 1366px | Navigation overlays at small widths; actions reflow; tables/diffs keep overflow inside their containers |
| Zoom and touch | Test 400% browser zoom, 200% text zoom and mobile touch targets | No clipping, data loss or page-level horizontal scroll; touch targets are at least 44 by 44 px where needed |
| States | Exercise loading, empty, validation, partial failure, permission denied, offline/rate-limited and success states | State is clear in text as well as color; live announcements are proportional to urgency |
| Motion and contrast | Enable reduced motion and audit contrast | Functional motion is suppressed when requested; text meets 4.5:1 (3:1 for large text) and controls meet 3:1 |

Reference: [Fluent 2 Accessibility](https://fluent2.microsoft.design/accessibility), [Layout](https://fluent2.microsoft.design/layout), [Design Tokens](https://fluent2.microsoft.design/design-tokens), and the mandatory [UI design contract](UI_DESIGN_CONTRACT.md).

## Repository text contract

`.gitattributes` is the canonical line-ending policy: text is normalized in Git, and shell scripts are always checked out with LF. Windows worktrees may use CRLF for ordinary text files without creating repository drift. `RepositoryTextContractTests` validates the line endings reported for Git index blobs by `git ls-files --eol`; it intentionally does not reject a platform-native worktree representation.

After changing or introducing the attributes policy, run `git add --renormalize .` once and review the staged diff. Keep `.gitattributes` committed so later additions are normalized automatically.

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
