#!/bin/sh
set -eu

if [ "${1:-}" = "reload" ]; then
    shift
    exec /usr/bin/caddy reload \
        --address "${CADDY_ADMIN_URL:-http://caddy:2019}" \
        "$@"
fi

exec /usr/bin/caddy "$@"
