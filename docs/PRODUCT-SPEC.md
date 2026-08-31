# Caddy UI 1.x Product Specification

## Purpose

Caddy UI is a fast, lightweight desktop-oriented web application for daily Caddy administration. It prioritizes reverse-proxy routes, operational health, observability, and practical edge protection. It is not a Docker management product and does not attempt to expose every Caddy directive through a form.

## Audience and deployment modes

- Personal and public home-lab installations.
- Multiple base domains with one default domain.
- Netcup is the first complete DNS and DDNS provider.
- Provider integrations are modular so more providers can be added later.
- Bundle mode uses a custom Caddy image with the Netcup and Caddy UI protection modules.
- Companion mode manages an existing standard Caddy installation. Features that require a custom Caddy module must fail safely when that module is unavailable.
- Both modes use exactly two containers: `caddy` and `caddy-ui`.
- The administration listener may be exposed only through an HTTPS reverse-proxy route with an exact configured public origin.
- The access-portal listener is internal-only and must never be published directly.

## Navigation

The primary navigation contains only daily task areas:

1. Overview
2. Routes
3. Domains & DNS
4. Access
5. Traffic
6. Requests
7. Security
8. System

Settings and About remain in the footer next to theme and sign-out controls. Role checks hide configuration areas the current user cannot change.

Specialized pages do not occupy permanent sidebar entries. Performance and route analytics are reached from Traffic, Live Log is reached from Requests, and health checks, jobs, notifications, backup/diagnostics, migration/rollback, and administrative security settings are grouped under System. Provider, DDNS, certificate, and guided setup tasks are grouped under Domains & DNS. Mobile uses the same hierarchy in the navigation drawer.

## Dashboard

The dashboard is ordered by operational importance:

1. Problems: unavailable public routes, failed upstreams, Caddy errors, DNS/DDNS failures, security warnings, and expiring certificates.
2. Compact observability KPIs: 24-hour requests, P95 response time, 5xx count, and active temporary security restrictions.
3. Traffic: request trend, status distribution, and busiest hosts.
4. Inventory: routes, domains, certificates, DNS providers, and system version.

It contains summaries and grouped charts only. Full analytics and logs belong to their dedicated workspaces.

## Routes

### Overview

- Routes are grouped by managed base domain so the public namespace is the primary navigation model.
- Each domain shows its route count, active count, default/disabled state, and a direct `+ Route` action.
- Route rows show the public host, route name, target, access state, enabled state, and primary actions without exposing certificate mode, sort order, or renderer details.
- Active routes with a concrete host expose an `Open` action for the public HTTPS URL. Wildcard hosts are not directly openable.
- Less common actions such as analytics, enable/disable, delete, import/export, and advanced route creation stay behind secondary or overflow actions.
- Quick create uses a desktop dialog and a full-screen mobile dialog. It asks only for a route name and upstream.
- Quick create derives a DNS-safe direct subdomain from the route name, selects wildcard certificate mode, creates an enabled reverse-proxy route at `/`, validates the full configuration, and activates it through the normal safe apply pipeline.
- If quick activation fails after persistence, the route remains stored but disabled so startup reconciliation cannot repeatedly apply a broken desired state.
- Advanced create/edit remains available for non-default behavior and preselects the domain from which it was opened.
- The route overview must collapse to a single-column layout on small screens without horizontal page scrolling; primary touch targets are at least 44 px high.

### Managed routes

The standard quick-create form shows only route name and upstream. The advanced form exposes domain, subdomain/host, route type, and optional behavior. Advanced settings contain:

- path matchers and path-based targets;
- request and response headers;
- multiple upstreams;
- load-balancing policy and active health checks;
- redirects;
- upstream TLS options;
- reusable access group;
- certificate override;
- selected safe reverse-proxy options.

The path prefix `/__caddy_ui_auth/*` is reserved for the access portal and cannot be assigned to a managed route. Generated access-portal handlers are placed before route-specific path matchers so a route cannot recursively protect its own login page.

Managed proxy routes remove untrusted `Remote-User` and internal portal identity headers. Protected routes overwrite `Remote-User` only with the identity returned by the internal portal authorization response.

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

Managed route files are reconciled through the current hardened renderer during startup. Revision restore regenerates route files from route metadata through the current renderer; legacy revisions without route metadata are not restored as raw authentication configuration.

## Access

- Reusable access groups can protect multiple routes.
- Version 1 uses a branded form login with username and password.
- A group can configure name, logo, help text, and accent color.
- Passwords are strongly hashed and never rendered back.
- Caddy UI and branded portal logins use persistent progressive brute-force protection keyed by securely resolved client IP and username.
- Portal authentication runs on a separate internal listener from the administration UI.
- Caddy authenticates to the portal listener with an automatically generated random secret stored in the protected UI database.
- Login POSTs require same-origin browser context and reject external or recursive return targets.
- Portal sessions use random tokens stored only as user-agent-bound hashes and expire after a configurable bounded lifetime.
- Authentication endpoints never pass through the protected route's own `forward_auth` handler, preventing recursive redirects.
- The architecture reserves provider types for forward-auth and OIDC without exposing unfinished controls.
- Future targets include Authentik, Authelia, Microsoft, Google, and GitHub.

## Analytics

Traffic is the primary analytics entry point. Specialized performance and per-route analytics remain available contextually without permanent sidebar entries.

Required metrics and behavior:

- request counts over time;
- average, P50, P95, and P99 response time;
- 4xx and 5xx rates;
- response traffic volume;
- busiest domains and endpoints;
- slowest endpoints;
- client/IP drill-downs that connect analytics, logs, and security history;
- time ranges `15m`, `1h`, `6h`, `24h`, `7d`, `30d`, `1y`, and custom;
- drill-down from metrics and charts into the corresponding filtered logs;
- clients classified as humans, bots/crawlers, internal checks, or unknown;
- request categories Pages, API, Assets, WebSocket, and Other;
- internal health and monitoring checks separated from normal user traffic;
- static assets included in total traffic but excluded by default from endpoint/performance ranking;
- numeric identifiers, UUIDs, and common opaque IDs normalized to `{id}` for endpoint aggregation while the exact raw path remains available in raw logs.

Charts are responsive, theme-aware, dependency-free, and shipped locally without CDN dependencies.

## Logs and traffic

Requests is the primary structured-log entry point. Live request mode is reached contextually from Requests rather than occupying a permanent sidebar entry.

Request log filters include:

- time range;
- domain/host;
- normalized endpoint and exact path search;
- HTTP method;
- status code/status class;
- minimum and maximum response time;
- client IP;
- user-agent/freetext;
- client type and request category.

Quick filters include 4xx, 5xx, slow requests, errors, recent requests, and bots. Active filters remain visible as removable chips and are encoded in the URL so views can be bookmarked or shared. Users can save named views. Administrators can export the active filtered request set as CSV or JSON. Redaction is preserved in exports.

Live request mode uses an efficient server-sent event stream with pause/resume behavior. It is opt-in and stops when the page is left.

Request persistence:

- full raw request metadata, including full client IP, retained for at least 30 days by default;
- raw request metadata includes timestamp, host, method, redacted URI, exact path, normalized endpoint, status, response bytes, response time, client IP, user-agent, client classification, and category;
- request/response bodies, cookies, and Authorization headers are never persisted;
- sensitive query values such as tokens, secrets, passwords, keys, auth codes, sessions, cookies, and signatures are redacted before persistence;
- administrators may extend the sensitive query-name list;
- hourly aggregates are generated while ingesting raw logs;
- data older than the raw retention window is compacted into daily aggregates without full IP addresses;
- aggregate retention defaults to one year and is configurable;
- raw Caddy log rotation remains external/configurable.

## Security

Security is a primary workspace for threats, blocked IPs, rate limits, and login protection. Administrative security configuration that is not needed for daily monitoring is grouped under System.

Protection levels:

- Off;
- Balanced (default);
- Strict;
- Custom.

Balanced defaults are intentionally generous for normal home-lab traffic and may be overridden per managed route. The protection layer supports:

- per-client request-rate limiting with burst allowance;
- temporary restrictions after repeated limit violations;
- dynamic administrator and automatic temporary IP restrictions;
- per-route inherit/off/custom policies;
- explicit trusted-proxy configuration;
- explicit allowlists;
- safely resolved client IPs that never trust `X-Forwarded-For` or `X-Real-IP` from an untrusted peer;
- WebSocket and streaming connections treated as request handshakes rather than long-running response-time failures;
- no automatic permanent IP or account bans.

Login protection:

- progressive delay begins after repeated failed attempts;
- default temporary login restriction begins after 10 failures;
- repeated attacks escalate from 15 minutes to one hour and up to 24 hours;
- successful sign-in clears the active failure counter;
- administrators can remove active temporary restrictions;
- unknown usernames perform a bounded constant-cost password verification;
- password length, accepted scrypt parameters, password-hash concurrency, and HTTP worker concurrency are bounded to limit memory-exhaustion attacks.

Threat detection observes recent request metadata for high request rates, repeated authorization failures, and scanning-like 404 patterns. The response follows `detect -> throttle/restrict -> temporary block`, records an explicit reason, and avoids automatic restrictions for private or explicitly allowlisted addresses.

Every administrator security-policy change and manual restriction action is recorded in the audit log. Security events preserve enough detail to explain why an automatic decision was made.

The custom protection handler is built into bundle mode so no CrowdSec, Redis, PostgreSQL, or third security container is required. Companion mode preserves hardened authentication and existing routes but reports the custom route guard as unavailable rather than writing an unsupported Caddy configuration.

## System

System is the parent workspace for technical and infrequently used operational tools.

- Caddy admin health, version, storage, certificates, and configuration state.
- Health checks and background jobs.
- Notification configuration.
- Backup and diagnostics.
- Migration and rollback.
- Administrative security settings.
- Validate configuration, safely reload Caddy, download diagnostics, view revisions, and restore a revision.
- No Docker socket and no container start/stop/update controls.
- Daily automatic backups and additional backups before updates/migrations.
- Restore is available to administrators.

## DNS

Domains & DNS is the parent workspace for domain, certificate, provider, DDNS, and guided setup tasks.

- Provider accounts and multiple domains are supported.
- Netcup supports listing, adding, editing, and deleting records plus DDNS status.
- Credentials may use environment references or encrypted application storage; secrets are never displayed.

## Administration

### Users and roles

- Administrator: full management, users, settings, restore, Custom Routes, analytics exports, and security policy changes.
- Editor: managed routes, DNS, access groups, and operational actions permitted by policy.
- Viewer: read-only status, routes, analytics, logs, security events, DNS, audit, and configuration previews.
- Login uses username/password and optional TOTP in version 1. Public deployments may require TOTP for every successful login after account setup.
- Passkeys are a future extension point, not an unfinished visible feature.
- Public mode requires an exact HTTPS origin and rejects other hosts or insecure requests.
- Login requests require same-origin browser context.
- Public session cookies use `Secure`, `HttpOnly`, `SameSite`, `Path=/`, and the `__Host-` prefix.
- Administration sessions are bound to the browser user agent and revoked on a binding mismatch.

### Audit

- Retained indefinitely with actor, time, request context, action, object type/id, before/after state, result, and correlated revision.
- Secrets and password hashes are redacted.
- Audit records are append-only through application APIs.

## Notifications

- Dashboard notifications, email, generic webhooks, Discord, and Telegram.
- Each channel and event is individually configurable.
- External channels are disabled by default.
- Discord webhook URLs and Telegram bot tokens are referenced through environment variables rather than stored as plaintext in SQLite.
- Initial events include public/down, upstream/down, certificate expiry, Caddy reload failure, DNS/DDNS failure, backup failure, update availability, security threats, and protection activation failures.
- Repeated security events are grouped/deduplicated before external notification where practical.
- Webhooks support ntfy and Home Assistant through generic JSON payloads.
