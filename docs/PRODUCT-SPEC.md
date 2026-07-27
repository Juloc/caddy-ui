# Caddy UI 1.0 Product Specification

## Purpose

Caddy UI is a fast, lightweight desktop-oriented web application for daily Caddy administration. It prioritizes reverse-proxy routes and operational health. It is not a Docker management product and does not attempt to expose every Caddy directive through a form.

## Audience and deployment modes

- Personal and public home-lab installations.
- Multiple base domains with one default domain.
- Netcup is the first complete DNS and DDNS provider.
- Provider integrations are modular so more providers can be added later.
- Bundle mode uses a custom Caddy image with the Netcup module.
- Companion mode manages an existing standard Caddy installation.
- Both modes use exactly two containers: `caddy` and `caddy-ui`.
- The administration listener may be exposed only through an HTTPS reverse-proxy route with an exact configured public origin.
- The access-portal listener is internal-only and is never published directly.

## Navigation

1. Dashboard
2. Routes
3. Access
4. Logs
5. System
6. DNS
7. Administration
   - Users
   - Audit Log
   - Settings

Administration is a collapsible group at the bottom of the desktop navigation. Mobile uses a navigation drawer.

## Dashboard

The dashboard is ordered by operational importance:

1. Problems: unavailable public routes, failed upstreams, Caddy errors, DNS/DDNS failures, and expiring certificates.
2. Traffic: request trend, status distribution, and busiest hosts.
3. Inventory: routes, domains, certificates, DNS providers, and system version.

It contains summaries and grouped charts only. Full logs belong to the Logs page.

## Routes

### Overview

- Compact flat table with sensible defaults and configurable columns.
- Default columns: state, host, upstream, route type, access group, requests, last change, actions.
- Public reachability and upstream health are separate states.
- Search, configurable columns, multi-selection, and bulk actions.
- Enable/disable, duplicate, import, export, and delete.
- Create/edit uses a desktop dialog and a full-screen mobile dialog.

### Managed routes

The basic form shows name, domain, host, and upstream. Advanced settings contain:

- path matchers and path-based targets;
- request and response headers;
- multiple upstreams;
- load-balancing policy and active health checks;
- redirects;
- upstream TLS options;
- reusable access group;
- selected safe reverse-proxy options.

The path prefix `/__caddy_ui_auth/*` is reserved for the access portal and cannot be assigned to a managed route. Generated access-portal handlers must be placed before route-specific path matchers.

### Custom routes

- Administrators may create a Custom Route containing a controlled Caddy snippet.
- Editors can only use managed forms.
- Viewers can only inspect previews and diffs.
- Full Caddyfile editing is not available.
- Existing unmanaged snippets are detected and can be imported through a preview-based wizard.
- Import never overwrites automatically.

### Apply workflow

1. Validate input.
2. Render a deterministic preview and diff.
3. Validate the complete Caddy configuration.
4. Atomically write managed snippets.
5. Reload Caddy.
6. Verify the admin API remains healthy.
7. Roll back automatically on failure.
8. Record the complete audit entry and revision.

Revision restore regenerates managed route files through the current secure renderer. Legacy revisions without route metadata are not restored as raw authentication configuration.

## Access

- Reusable access groups can protect multiple routes.
- Version 1 uses a branded form login with username and password.
- A group can configure name, logo, help text, and accent color.
- Passwords are strongly hashed and never rendered back.
- The architecture reserves provider types for forward-auth and OIDC without exposing unfinished controls.
- Future targets include Authentik, Authelia, Microsoft, Google, and GitHub.
- Portal authentication runs on a separate internal listener from the administration UI.
- Caddy authenticates to the portal listener with an automatically generated random secret stored in the protected UI database.
- Login POSTs require same-origin browser context and reject external or recursive return targets.
- Portal sessions are random, hashed at rest, short-lived, and bound to the browser user agent.
- Failed-login limits persist through audit data and apply per address and identity.
- Incoming identity headers are removed or overwritten before managed proxy routes receive them.
- Authentication endpoints never pass through the protected route's own `forward_auth` handler, preventing recursive redirects.

## Logs and traffic

The Logs page has tabs for:

- Access;
- Caddy/System;
- DDNS/DNS.

It supports live updates, pause/resume, text search, structured filters, severity filters, host/status filters, and download of the currently filtered view.

Traffic retention:

- detailed values for 30 days;
- daily aggregates after 30 days;
- monthly aggregates after one year;
- compact aggregates retained indefinitely;
- raw log rotation remains external/configurable.

## System

- Caddy admin health, version, storage, certificates, and configuration state.
- Validate configuration, safely reload Caddy, download diagnostics, view revisions, and restore a revision.
- No Docker socket and no container start/stop/update controls.
- Daily automatic backups and additional backups before updates/migrations.
- Restore is available to administrators.

## DNS

- DNS remains a dedicated secondary navigation item.
- Provider accounts and multiple domains are supported.
- Netcup supports listing, adding, editing, and deleting records plus DDNS status.
- Credentials may use environment references or encrypted application storage; secrets are never displayed.

## Administration

### Users and roles

- Administrator: full management, users, settings, restore, and Custom Routes.
- Editor: managed routes, DNS, access groups, and operational actions permitted by policy.
- Viewer: read-only status, routes, logs, DNS, audit, and configuration previews.
- Login uses username/password and optional TOTP in version 1.
- Passkeys are a future extension point, not an unfinished visible feature.
- Public mode requires an exact HTTPS origin and rejects other hosts or insecure requests.
- Login requests require same-origin browser context.
- Session cookies use `Secure`, `HttpOnly`, `SameSite`, `Path=/`, and the `__Host-` prefix when served publicly.
- Administration sessions are bound to the browser user agent and revoked on a binding mismatch.
- Public operators can require TOTP for every successful login after account setup.
- Password verification uses constant-cost dummy verification for unknown identities, bounded scrypt parameters, bounded hashing concurrency, and bounded HTTP worker concurrency.
- Failed-login limits persist across application restarts through audit records.

### Audit

- Retained indefinitely with actor, time, request context, action, object type/id, before/after state, result, and correlated revision.
- Secrets and password hashes are redacted.
- Audit records are append-only through application APIs.

## Notifications

- Dashboard notifications, email, and generic webhooks.
- Each channel and event is individually configurable.
- Initial events: public/down, upstream/down, certificate expiry, Caddy reload failure, DNS/DDNS failure, backup failure, and update availability.
- Webhooks support ntfy and Home Assistant through generic JSON payloads.

## Persistence and migration

- SQLite in the existing persistent UI volume.
- WAL mode, foreign keys, bounded busy timeout, and explicit migrations.
- Existing JSON provider configuration and managed route metadata are imported automatically.
- Migration creates a backup first, validates imported data, and rolls back on failure.
- Existing portal sessions may be invalidated when session-binding rules are strengthened.

## Removed scope

- App templates and generated Docker Compose snippets.
- Docker socket integration.
- Full raw Caddyfile editor.
- Heavy SPA frameworks.
- Unfinished provider controls.
