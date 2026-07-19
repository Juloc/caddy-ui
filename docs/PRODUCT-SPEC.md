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

## Access

- Reusable access groups can protect multiple routes.
- Version 1 uses a branded form login with username and password.
- A group can configure name, logo, help text, and accent color.
- Passwords are strongly hashed and never rendered back.
- The architecture reserves provider types for forward-auth and OIDC without exposing unfinished controls.
- Future targets include Authentik, Authelia, Microsoft, Google, and GitHub.

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

## Removed scope

- App templates and generated Docker Compose snippets.
- Docker socket integration.
- Full raw Caddyfile editor.
- Heavy SPA frameworks.
- Unfinished provider controls.
