# Security Model

- The administration UI is not intended for direct public exposure.
- Docker socket access is prohibited.
- Caddy admin API stays on an internal network.
- Initial administrator creation uses environment bootstrap values once, then stores only a strong password hash.
- Password hashing uses `scrypt` with per-password salts and versioned parameters.
- Sessions are random, server-side, expiring, revocable, and stored hashed.
- Cookies are HttpOnly, SameSite=Strict, and Secure when served over HTTPS.
- State changes require per-session CSRF tokens.
- Authorization is enforced in controllers and application services.
- Login attempts are rate-limited and security events are audited.
- Provider credentials remain environment references and are never persisted as literal secret values.
- TOTP secrets are sensitive database content; protect and back up the UI volume with host-level encryption and restricted filesystem permissions.
- Diagnostics, logs, audit payloads, exports, and configuration diffs redact secrets.
- Managed route changes are validated, written atomically, reloaded, verified, and rolled back on failure.
- Custom Routes are administrator-only and validated by Caddy before activation.
- Path handling resolves and verifies allowed directories to prevent traversal.
- Security headers include CSP, frame denial, MIME sniffing protection, referrer policy, and restrictive permissions policy.
