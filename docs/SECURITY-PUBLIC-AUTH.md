# Public Authentication Checklist

- Generate and configure `CADDY_UI_PROXY_SECRET` before creating protected routes.
- Enable administrator TOTP through the loopback-only setup endpoint.
- Set the exact HTTPS `CADDY_UI_PUBLIC_URL` and restart both containers.
- Confirm port 8098 remains bound to `127.0.0.1` only.
- Confirm port 2019 is not published.
- Verify the public administrator host matches `CADDY_UI_PUBLIC_URL`.
- Verify protected route requests redirect to the portal and return after successful login.
- Verify direct access to port 8098 from outside the host fails.
- Verify old sessions are invalidated after the security migration.
- Keep client IP binding disabled unless client addresses are stable.
