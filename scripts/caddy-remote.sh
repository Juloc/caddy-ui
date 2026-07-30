#!/bin/sh
set -eu

if [ "${1:-}" = "reload" ]; then
    shift
    admin_address="${CADDY_ADMIN_URL:-caddy:2019}"
    admin_address="${admin_address#http://}"
    admin_address="${admin_address#https://}"

    exec /usr/bin/caddy reload \
        --address "$admin_address" \
        "$@"
fi

exec /usr/bin/caddy "$@"
