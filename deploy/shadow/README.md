# Caddy UI .NET Shadow Deployment

This stack runs the integrated .NET/PostgreSQL implementation beside the current Python/SQLite deployment. It does not replace the productive Caddy or Caddy UI containers.

## Safety boundary

The shadow stack:

- builds `Dockerfile.dotnet` locally;
- uses a dedicated PostgreSQL volume;
- binds the administration UI only to `127.0.0.1:18098` by default;
- does not publish the access-portal port;
- mounts the productive Caddy log directory read-only;
- mounts the legacy SQLite file read-only;
- uses a Docker-internal network;
- has no Docker socket;
- keeps route, DNS, worker, IP intelligence, risk and blocklist writes disabled;
- keeps `Cutover:Enabled=false`;
- does not modify the productive Caddy configuration, certificates, routes or ports.

## 1. Prepare the environment

```sh
cp deploy/shadow/.env.example deploy/shadow/.env
chmod 600 deploy/shadow/.env
```

Set two different random passwords with at least 20 characters. Configure absolute paths for:

- `CADDY_UI_SHADOW_LOG_DIR`: host directory containing the productive Caddy JSON access log;
- `CADDY_UI_SHADOW_LEGACY_SQLITE`: current legacy Caddy UI SQLite database.

The access log is expected as `access.log` inside the directory unless `CADDY_UI_SHADOW_ACCESS_LOG` specifies another file name.

## 2. Run the preflight

```sh
chmod +x scripts/shadow-preflight.sh
./scripts/shadow-preflight.sh deploy/shadow/.env
```

The preflight only validates commands, paths, secrets, port availability and the rendered Compose model. It does not create or start containers.

## 3. Build and start

```sh
docker compose \
  --env-file deploy/shadow/.env \
  -f deploy/shadow/docker-compose.yml \
  build

docker compose \
  --env-file deploy/shadow/.env \
  -f deploy/shadow/docker-compose.yml \
  up -d
```

The `migrate` service applies PostgreSQL migrations once. The web service starts only after the migration exits successfully.

## 4. Verify the isolated UI

On the server:

```sh
curl --fail http://127.0.0.1:18098/health/live
curl --fail http://127.0.0.1:18098/health/ready
```

From a workstation, use an SSH tunnel:

```sh
ssh -L 18098:127.0.0.1:18098 your-server
```

Then open `http://127.0.0.1:18098`.

Do not add this service to the productive Caddy routes during the observation period.

## 5. Shadow observation

Keep the stack running for at least the configured `CADDY_UI_SHADOW_MIN_HOURS`, default 24 hours. During this period verify:

- ingestion checkpoint advances;
- latest event lag remains within the configured limit;
- log rotation and container restart do not duplicate events;
- one page load with many framework assets remains one pageview and many requests;
- memory and PostgreSQL growth remain bounded;
- no route, DNS, blocklist or Caddy files are changed;
- no provider API calls occur while the corresponding workers are disabled.

Create a fresh PostgreSQL backup before every migration or cutover rehearsal.

## 6. Statistics comparison

Create a legacy snapshot and compare exactly the same closed UTC interval. The required metrics are:

- requests;
- pageviews;
- sessions;
- clients;
- HTTP 5xx errors.

The default accepted deviation is five percent per metric. A failed comparison blocks the cutover.

## 7. Stop without deleting evidence

```sh
docker compose \
  --env-file deploy/shadow/.env \
  -f deploy/shadow/docker-compose.yml \
  stop
```

Do not use `down -v` while validation evidence, PostgreSQL data or cutover manifests are still needed.

## Production cutover

The shadow Compose file is not the production deployment file. Port switching, final SQLite import, Caddy validation and rollback follow `docs/CUTOVER_RUNBOOK.md` and require an explicit maintenance window.
