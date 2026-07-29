#!/bin/sh
set -eu

ENV_FILE="${1:-deploy/shadow/.env}"
COMPOSE_FILE="deploy/shadow/docker-compose.yml"

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "Required command '$1' is missing."
}

require_value() {
    name="$1"
    eval "value=\${$name:-}"
    [ -n "$value" ] || fail "$name is not set in $ENV_FILE."
}

require_absolute_path() {
    name="$1"
    eval "value=\${$name:-}"
    case "$value" in
        /*) ;;
        *) fail "$name must be an absolute host path." ;;
    esac
}

[ -f "$ENV_FILE" ] || fail "Environment file '$ENV_FILE' does not exist. Copy deploy/shadow/.env.example first."
[ -f "$COMPOSE_FILE" ] || fail "Compose file '$COMPOSE_FILE' does not exist."

require_command docker
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is unavailable."

set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a

for variable in \
    CADDY_UI_SHADOW_VERSION \
    CADDY_UI_SHADOW_DB_NAME \
    CADDY_UI_SHADOW_DB_USER \
    CADDY_UI_SHADOW_DB_PASSWORD \
    CADDY_UI_SHADOW_ADMIN_USERNAME \
    CADDY_UI_SHADOW_ADMIN_PASSWORD \
    CADDY_UI_SHADOW_LOG_DIR \
    CADDY_UI_SHADOW_LEGACY_SQLITE
 do
    require_value "$variable"
 done

case "$CADDY_UI_SHADOW_VERSION" in
    2.0.0-beta.[0-9]*) ;;
    *) fail "CADDY_UI_SHADOW_VERSION must be an explicit 2.0.0-beta.N version." ;;
esac

require_absolute_path CADDY_UI_SHADOW_LOG_DIR
require_absolute_path CADDY_UI_SHADOW_LEGACY_SQLITE

[ -d "$CADDY_UI_SHADOW_LOG_DIR" ] || fail "CADDY_UI_SHADOW_LOG_DIR is not a readable directory."
[ -r "$CADDY_UI_SHADOW_LOG_DIR" ] || fail "CADDY_UI_SHADOW_LOG_DIR is not readable."
[ -f "$CADDY_UI_SHADOW_LEGACY_SQLITE" ] || fail "CADDY_UI_SHADOW_LEGACY_SQLITE is not a regular file."
[ -r "$CADDY_UI_SHADOW_LEGACY_SQLITE" ] || fail "CADDY_UI_SHADOW_LEGACY_SQLITE is not readable."

ACCESS_LOG="${CADDY_UI_SHADOW_ACCESS_LOG:-access.log}"
case "$ACCESS_LOG" in
    */*) fail "CADDY_UI_SHADOW_ACCESS_LOG must be a file name inside the configured log directory." ;;
esac
[ -f "$CADDY_UI_SHADOW_LOG_DIR/$ACCESS_LOG" ] || fail "Access log '$CADDY_UI_SHADOW_LOG_DIR/$ACCESS_LOG' does not exist."
[ -r "$CADDY_UI_SHADOW_LOG_DIR/$ACCESS_LOG" ] || fail "Access log '$CADDY_UI_SHADOW_LOG_DIR/$ACCESS_LOG' is not readable."

case "$CADDY_UI_SHADOW_DB_PASSWORD" in
    replace-*|change-*|password|caddy_ui|caddy-ui) fail "Choose a non-default PostgreSQL password." ;;
esac
case "$CADDY_UI_SHADOW_ADMIN_PASSWORD" in
    replace-*|change-*|password|admin) fail "Choose a non-default shadow administrator password." ;;
esac

[ "${#CADDY_UI_SHADOW_DB_PASSWORD}" -ge 20 ] || fail "PostgreSQL password must contain at least 20 characters."
[ "${#CADDY_UI_SHADOW_ADMIN_PASSWORD}" -ge 20 ] || fail "Administrator password must contain at least 20 characters."
[ "$CADDY_UI_SHADOW_DB_PASSWORD" != "$CADDY_UI_SHADOW_ADMIN_PASSWORD" ] || fail "PostgreSQL and administrator passwords must be different."

BIND_ADDRESS="${CADDY_UI_SHADOW_BIND_ADDRESS:-127.0.0.1}"
ADMIN_PORT="${CADDY_UI_SHADOW_ADMIN_PORT:-18098}"
case "$ADMIN_PORT" in
    *[!0-9]*|'') fail "CADDY_UI_SHADOW_ADMIN_PORT must be numeric." ;;
esac
[ "$ADMIN_PORT" -ge 1024 ] && [ "$ADMIN_PORT" -le 65535 ] || fail "CADDY_UI_SHADOW_ADMIN_PORT must be between 1024 and 65535."

if command -v ss >/dev/null 2>&1 && ss -H -ltn "sport = :$ADMIN_PORT" 2>/dev/null | grep -q .; then
    fail "TCP port $ADMIN_PORT is already in use."
fi

MIN_HOURS="${CADDY_UI_SHADOW_MIN_HOURS:-24}"
MAX_BACKUP_AGE="${CADDY_UI_SHADOW_MAX_BACKUP_AGE_HOURS:-24}"
TOLERANCE="${CADDY_UI_SHADOW_TOLERANCE_PERCENT:-5}"
for pair in "MIN_HOURS:$MIN_HOURS" "MAX_BACKUP_AGE:$MAX_BACKUP_AGE" "TOLERANCE:$TOLERANCE"; do
    name=${pair%%:*}
    value=${pair#*:}
    case "$value" in
        *[!0-9]*|'') fail "$name must be a non-negative integer." ;;
    esac
 done
[ "$MIN_HOURS" -ge 1 ] || fail "CADDY_UI_SHADOW_MIN_HOURS must be at least 1."
[ "$MAX_BACKUP_AGE" -ge 1 ] || fail "CADDY_UI_SHADOW_MAX_BACKUP_AGE_HOURS must be at least 1."
[ "$TOLERANCE" -le 100 ] || fail "CADDY_UI_SHADOW_TOLERANCE_PERCENT must not exceed 100."

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" config >/dev/null

printf 'Shadow preflight successful.\n'
printf 'Image: ghcr.io/juloc/caddy-ui-dotnet-companion:%s\n' "$CADDY_UI_SHADOW_VERSION"
printf 'Admin UI will bind to %s:%s.\n' "$BIND_ADDRESS" "$ADMIN_PORT"
printf 'Legacy SQLite and Caddy logs will be mounted read-only.\n'
printf 'Routing, DNS, workers, IP intelligence, risk processing, blocklist writes, and cutover remain disabled.\n'
printf 'The readiness freshness threshold is fixed at 15 minutes by the application.\n'
printf '\nNext commands:\n'
printf '  docker compose --env-file %s -f %s pull\n' "$ENV_FILE" "$COMPOSE_FILE"
printf '  docker compose --env-file %s -f %s up -d\n' "$ENV_FILE" "$COMPOSE_FILE"
printf '  curl --fail http://%s:%s/health/ready\n' "$BIND_ADDRESS" "$ADMIN_PORT"
