# Caddy UI .NET Shadow Deployment

This stack runs the .NET/PostgreSQL implementation beside the productive Python/SQLite deployment. It does not replace Caddy, the productive Caddy UI containers, routes, certificates or ports.

## Safety boundary

- immutable image `ghcr.io/juloc/caddy-ui-dotnet-companion:2.0.0-beta.1`
- dedicated PostgreSQL and state volumes
- administration bound to `127.0.0.1:18098` by default
- portal port `8099` is not published
- Caddy logs and legacy SQLite are mounted read-only
- migration and web containers use read-only root filesystems
- internal Docker network and no Docker socket
- routing, DNS, operations worker, IP intelligence, risk processing and blocklist writes disabled
- `Cutover:Enabled=false`

## Prepare

```sh
cp deploy/shadow/.env.example deploy/shadow/.env
chmod 600 deploy/shadow/.env
```

Set two different random passwords with at least 20 characters. Configure absolute host paths for the productive Caddy log directory and the legacy SQLite file. Keep `CADDY_UI_SHADOW_VERSION=2.0.0-beta.1` unchanged for the first rehearsal.

For direct LAN testing, set `CADDY_UI_SHADOW_BIND_ADDRESS` to the server LAN address. The safe default remains loopback.

## Validate and start

```sh
chmod +x scripts/shadow-preflight.sh
./scripts/shadow-preflight.sh deploy/shadow/.env

docker compose \
  --env-file deploy/shadow/.env \
  -f deploy/shadow/docker-compose.yml \
  pull

docker compose \
  --env-file deploy/shadow/.env \
  -f deploy/shadow/docker-compose.yml \
  up -d
```

The `migrate` service applies PostgreSQL migrations once. The web service starts only after migration succeeds.

## Verify

```sh
curl --fail http://127.0.0.1:18098/health/live
curl --fail http://127.0.0.1:18098/health/ready
```

For loopback deployments, access the UI through an SSH tunnel:

```sh
ssh -L 18098:127.0.0.1:18098 your-server
```

## Observation

Run the stack for at least 24 hours. Verify advancing checkpoints, request freshness below 15 minutes, rotation and restart idempotency, bounded PostgreSQL growth, correct request/pageview separation and zero productive writes.

Create a PostgreSQL backup before migration or cutover rehearsals. A current backup, the legacy statistics snapshot and successful comparison are required by the cutover gate.

## Stop without deleting evidence

```sh
docker compose \
  --env-file deploy/shadow/.env \
  -f deploy/shadow/docker-compose.yml \
  stop
```

Do not use `down -v` while PostgreSQL data, manifests or validation evidence are still required. Production switching follows `docs/CUTOVER_RUNBOOK.md` only after the shadow checklist is complete.
