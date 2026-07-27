from __future__ import annotations

from .domain import AccessGroup
from .views import e


def login(csrf: str, error: str = "") -> bytes:
    error_html = f'<div class="notice error" role="alert">{e(error)}</div>' if error else ""
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Sign in · Caddy UI</title><link rel="stylesheet" href="/static/app.css"></head>
<body><main class="login-shell"><section class="login-card"><div class="brand-mark">C</div><h1>Sign in to Caddy UI</h1><p class="muted">Manage routes, DNS, access and system health.</p>{error_html}<form method="post" action="/login" class="stack"><input type="hidden" name="login_csrf" value="{e(csrf)}"><label>Username<input name="username" autocomplete="username" required autofocus maxlength="80"></label><label>Password<input name="password" type="password" autocomplete="current-password" required maxlength="512"></label><label>TOTP code <span class="muted">(required for public access)</span><input name="totp" inputmode="numeric" autocomplete="one-time-code" pattern="[0-9]{{6}}" maxlength="6"></label><button class="primary" type="submit">Sign in</button></form></section></main></body></html>""".encode("utf-8")


def portal_login(group: AccessGroup, csrf: str, error: str = "", return_to: str = "/") -> bytes:
    error_html = f'<div class="notice error" role="alert">{e(error)}</div>' if error else ""
    logo = (
        f'<img class="portal-logo" src="{e(group.logo_data)}" alt="">'
        if group.logo_data
        else '<div class="brand-mark">C</div>'
    )
    return f"""<!doctype html><html lang="en" style="--accent:{e(group.accent)}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{e(group.title)}</title><link rel="stylesheet" href="/__caddy_ui_auth/static/app.css"></head><body><main class="login-shell"><section class="login-card">{logo}<h1>{e(group.title)}</h1><p class="muted">{e(group.help_text)}</p>{error_html}<form method="post" action="/__caddy_ui_auth/login" class="stack"><input type="hidden" name="login_csrf" value="{e(csrf)}"><input type="hidden" name="group" value="{e(group.id)}"><input type="hidden" name="return_to" value="{e(return_to)}"><label>Username<input name="username" autocomplete="username" required maxlength="80"></label><label>Password<input name="password" type="password" autocomplete="current-password" required maxlength="512"></label><button class="primary" type="submit">Sign in</button></form></section></main></body></html>""".encode("utf-8")
