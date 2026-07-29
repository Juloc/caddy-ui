#!/bin/sh
set -eu

command="${1:-web}"

case "$command" in
    web)
        shift || true
        exec dotnet /app/CaddyUi.Web.dll "$@"
        ;;
    migrate)
        shift || true
        exec dotnet /app/migration/CaddyUi.Migration.dll "$@"
        ;;
    caddy)
        shift || true
        exec /usr/bin/caddy "$@"
        ;;
    *)
        exec "$@"
        ;;
esac
