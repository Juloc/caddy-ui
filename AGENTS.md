# Caddy UI Agent Guide

## Product direction

Caddy UI is a compact self-hosted control plane for Caddy. Its primary jobs are managed reverse-proxy routes, DNS and certificate setup, operational status, analytics, logs, access control and safe configuration rollout.

## Engineering rules

- The application runtime is .NET 10 with ASP.NET Core Razor Pages, EF Core and PostgreSQL 17.
- Go is limited to the custom Caddy build, DNS modules and the Caddy UI request guard.
- Do not add or restore a parallel Python runtime.
- Keep the UI server-rendered. Use JavaScript only where progressive enhancement is required.
- Never require Docker socket access.
- Separate contracts, domain logic, application services, infrastructure and presentation.
- Remove replaced or dead implementations completely. Do not leave compatibility copies, duplicate CSS systems or generic final-fix scripts.
- Preserve user data. Schema and configuration changes require backup, migration validation and rollback paths.
- The legacy SQLite database is read-only migration input, not an active persistence backend.
- Validate the complete Caddy configuration before activation. Apply changes atomically and restore the previous revision if reload fails.
- Never render secrets back to the browser or persist resolved secret values in PostgreSQL, logs, reports, diffs or generated Caddy files.
- Use English for code, documentation, UI text, tests, logs and commit messages.
- Format C# with `dotnet format` and Go with `gofmt`.
- State-changing requests require authorization and CSRF protection.

## UI rules

- Follow `docs/UI_DESIGN_CONTRACT.md`.
- Use a calm Microsoft Fluent 2 / Windows 11 visual language.
- Tables, logs, forms and editors are flat and compact.
- Use one icon family, clear focus states, semantic status colors and accessible contrast.
- Default density is compact.
- Theme selector has exactly System, Light and Dark. System is the default.
- Desktop edit/create flows use dialogs. On small screens the same flows become full-screen.
- Do not turn the application into a marketing-style SaaS dashboard.

## Verification

Before declaring a work item complete:

1. Run `dotnet restore`, `dotnet format --verify-no-changes`, Release build and all .NET tests.
2. Run `gofmt`, `go mod tidy` and `go test ./...` without repository changes.
3. Build the `dotnet-companion` and `dotnet-bundle` targets from `Dockerfile.dotnet`.
4. Validate the production Compose model, PostgreSQL migration, legacy SQLite import and health endpoints.
5. Validate generated Caddy configuration, remote reload and rollback behavior.
6. Check desktop and mobile layouts, keyboard navigation, themes and empty/error/loading states.
7. Confirm removed features and dead code are actually gone.
8. Update the relevant architecture, operations and release documentation.
