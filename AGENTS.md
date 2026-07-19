# Caddy UI Agent Guide

## Product direction

Caddy UI is a lightweight, public home-lab control plane for Caddy. Its primary jobs are managed reverse-proxy routes, operational status, logs, DNS, and access control. It must remain useful with an existing standard Caddy installation while providing a first-class Netcup bundle.

## Engineering rules

- Keep the runtime lightweight: Python standard library, SQLite, server-rendered HTML, and small dependency-free JavaScript.
- Keep Caddy and Caddy UI as two containers. Never require Docker socket access.
- Separate transport, application services, persistence, provider integrations, Caddy rendering, and presentation.
- Do not add a feature without a concrete product requirement in `docs/PRODUCT-SPEC.md`.
- Remove replaced or dead implementations completely. Do not leave compatibility copies, duplicate CSS systems, or generic final-fix scripts.
- Preserve user data. All schema and configuration changes require an automatic backup, migration validation, and rollback path.
- Validate generated Caddy configuration before activation. Apply changes atomically and restore the previous revision if reload fails.
- Never render secrets back to the browser or write plaintext passwords to managed Caddy snippets.
- Use English for code, documentation, UI text, tests, logs, and commit messages.
- Follow standard Python and Go conventions. Format Go with `gofmt`.
- Keep APIs explicit and permission-checked. State-changing requests require CSRF protection.
- Prefer focused modules and functions. Avoid catch-all modules and hidden global mutable state.

## UI rules

- Follow `docs/DESIGN.md`.
- Use a calm Microsoft Fluent 2 / Windows 11 visual language.
- Dashboard cards may be elevated. Tables, logs, forms, and editors are flat and compact.
- Use one Fluent icon family, clear focus states, semantic status colors, and accessible contrast.
- Default density is compact.
- Theme selector has exactly three states: System, Light, Dark. System is the default.
- Desktop edit/create flows use dialogs. On small screens the same dialogs become full-screen.
- Do not turn the application into a typical marketing-style SaaS dashboard.

## Verification

Before declaring a work item complete:

1. Run all Python and Go tests.
2. Run syntax/compile checks.
3. Build the container image.
4. Validate generated Caddy configuration and rollback behavior.
5. Check desktop and mobile layouts, keyboard navigation, light/dark themes, and empty/error/loading states.
6. Check that removed features and dead code are actually gone.
7. Update `docs/BACKLOG.md` and the relevant design/architecture documents.
