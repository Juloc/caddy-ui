# Security Model

## Trust boundaries

- Internet traffic reaches Caddy only on ports 80 and 443.
- Caddy UI port 8098 is bound to host loopback for local maintenance and is otherwise reachable only on the private Docker network.
- Caddy admin API stays on the internal network and port 2019 is never published.
- Protected routes and the public Caddy UI route use a shared random `CADDY_UI_PROXY_SECRET` passed as an internal request header.
- Caddy overwrites the internal secret for trusted internal requests and strips client-supplied identity and secret headers before application upstreams.
- Caddy UI accepts public and portal requests only from configured trusted proxy CIDRs with the correct shared secret and HTTPS forwarding metadata.
- Docker socket access is prohibited.

## Administrator authentication

- Public administration requires an exact `CADDY_UI_PUBLIC_URL` HTTPS origin.
- The dedicated managed Caddy UI route is bootstrapped only when that origin is configured and the host is unused.
- TOTP is required by default in public mode.
- Login and state-changing requests require same-origin metadata; login forms additionally use a pre-authentication CSRF cookie/token pair.
- Authenticated state-changing requests keep the server-side per-session CSRF token checks.
- Sessions use random tokens stored only as SHA-256 hashes, are revocable, and expire server-side.
- Sessions are bound to the browser user agent and may optionally be bound to client IP.
- Public administrator sessions are capped at eight hours.

## Access portals

- Portal endpoints are rendered outside path-specific application matchers.
- Authorization uses Caddy `forward_auth` over the private Docker network with the shared internal secret.
- Client-supplied `Remote-User` and internal-secret headers are removed before authorization.
- Portal sessions use random hashed tokens, secure strict cookies, an eight-hour default lifetime, and browser user-agent binding.
- Redirect targets are restricted to safe same-origin paths.

## Credential and abuse protection

- New administrator and portal passwords require 14 to 512 characters.
- Passwords use salted `scrypt` with parameters embedded in each hash for compatibility and upgrades.
- Login failures are tracked by account and client address in SQLite, so restart does not reset limits.
- Administrator and portal login attempts are audited without logging passwords or tokens.
- The first hardened startup backs up the database, migrates portal session metadata, and invalidates sessions created before the hardened trust model.

## Secrets and data handling

- Initial administrator creation uses environment bootstrap values once, then stores only a password hash.
- Provider credentials remain environment references and are never persisted as literal secret values.
- TOTP secrets are sensitive database content; protect and back up the UI volume with host-level encryption and restricted filesystem permissions.
- Diagnostics, logs, audit payloads, exports, and configuration diffs redact secrets.
- Portal branding accepts only bounded text and safe raster image data or HTTPS image references.

## Configuration safety

- Managed route changes are validated, written atomically, reloaded, verified, and rolled back on failure.
- Custom Routes are administrator-only and validated by Caddy before activation.
- Path handling resolves and verifies allowed directories to prevent traversal.
- Security headers include CSP, HSTS for public/protected origins, frame denial, MIME sniffing protection, referrer policy, restrictive permissions policy, COOP, CORP, and no-index directives.

## Required deployment rules

- Never publish Caddy admin port 2019.
- Never change the Caddy UI bind from `127.0.0.1:8098` to a public interface.
- Keep `.env` outside Git and use a unique generated proxy secret.
- Enable TOTP locally before setting `CADDY_UI_PUBLIC_URL`.
- Restart both containers after changing the shared secret or public origin.
