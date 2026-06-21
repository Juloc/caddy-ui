# Caddy UI

Public home-lab Caddy image with:

- Caddy built with a local Netcup DNS provider module
- wildcard certificates through ACME DNS-01
- Netcup DDNS for changing home WAN IPv4 addresses
- a small web UI for managed reverse proxy routes
- status views for Caddy admin, route count and stored certificates

The published image is:

```text
ghcr.io/juloc/caddy-ui:latest
```

The server deployment needs only `compose.yml` and `.env`. No local Dockerfile build is required.

## Architecture

The same image exposes four commands:

| Command | Purpose |
| --- | --- |
| `caddy` | runs the custom Caddy binary |
| `web` | runs the Caddy UI |
| `ddns` | runs the Netcup DDNS updater |
| `init-caddyfile` | writes the default wildcard Caddyfile into a Docker volume |

The custom Caddy module is registered as:

```text
dns.providers.netcup
```

It is implemented in this repository and does not import `github.com/caddy-dns/netcup`.

## Quick Start

Create `.env` from `.env.example`, set real values, then run:

```sh
docker network create proxy
docker compose --env-file .env up -d
```

Open the UI:

```text
http://<server-ip>:8098
```

Only forward ports `80/tcp` and `443/tcp` from the router to the Caddy host. Forward `443/udp` only if you want HTTP/3.

## Environment

```env
DOMAIN=example.com
ACME_EMAIL=admin@example.com

NETCUP_CUSTOMER_NUMBER=123456
NETCUP_API_KEY=...
NETCUP_API_PASSWORD=...

NETCUP_DDNS_DOMAIN=example.com
NETCUP_DDNS_HOSTS=@,*
NETCUP_DDNS_INTERVAL=300s

CADDY_UI_USERNAME=admin
CADDY_UI_PASSWORD=change-me
```

Create the Netcup DNS records once before starting DDNS:

| Host | Type | Destination |
| --- | --- | --- |
| `@` | `A` | current WAN IPv4 |
| `*` | `A` | current WAN IPv4 |

The DDNS updater keeps those records pointed at the current public IPv4.

## Generated Caddyfile

`init-caddyfile` writes this structure to `/etc/caddy/Caddyfile`:

```caddyfile
{
    email {$ACME_EMAIL}
    admin 0.0.0.0:2019
}

{$DOMAIN}, *.{$DOMAIN} {
    tls {
        dns netcup {
            customer_number {env.NETCUP_CUSTOMER_NUMBER}
            api_key {env.NETCUP_API_KEY}
            api_password {env.NETCUP_API_PASSWORD}
        }
        propagation_timeout 600s
        resolvers 1.1.1.1 8.8.8.8
    }

    import /etc/caddy/routes/*.caddy

    handle {
        respond "Service not configured" 404
    }
}
```

## How Routes Work

The UI writes managed snippets to `/etc/caddy/routes/*.caddy` in a shared Docker volume. Caddy imports those snippets and reloads through its internal admin API.

When `DOMAIN` is set, the route host is optional. A route named `app` defaults to `app.example.com`.

Example route:

```caddyfile
# managed-by caddy-ui
# caddy-ui-route: {"host":"","name":"app","tls_skip_verify":false,"upstream":"app.internal:5055"}
@app host app.example.com
handle @app {
    reverse_proxy app.internal:5055
}
```

The UI also reads Caddy certificate metadata from `/data` and shows useful non-secret status information: Caddy admin reachability, storage paths, route counts, certificate names, wildcard certificates and expiry dates.

## Publishing

Pushing to `main` publishes:

```text
ghcr.io/juloc/caddy-ui:latest
```

Tags like `v1.0.0` publish matching image tags as well.

After the first push, make the GHCR package public in GitHub if the server should pull it without `docker login`.

## Security Notes

- Set `CADDY_UI_PASSWORD`.
- Do not port-forward the UI.
- Do not port-forward Caddy's admin port `2019`.
- Put admin apps like Proxmox, Portainer, SABnzBD, Radarr and Sonarr behind VPN or another strong auth layer.
