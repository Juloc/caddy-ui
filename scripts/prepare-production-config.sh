#!/bin/sh
set -eu

root_config="${CADDY_UI_CADDY_ROOT_CONFIG:-/etc/caddy/Caddyfile}"
routes_dir="${CADDY_UI_ROUTES_DIR:-/etc/caddy/routes}"
managed_fragment="${CADDY_UI_MANAGED_ROUTES_PATH:-${routes_dir}/site-managed-routes.caddy}"
legacy_dir="${CADDY_UI_LEGACY_ROUTES_DIR:-${routes_dir}/legacy-dotnet-cutover}"

mkdir -p "$routes_dir" "$legacy_dir"

if [ ! -f "$root_config" ]; then
    cat >"$root_config" <<'EOF'
{
    email {$ACME_EMAIL}
    admin 0.0.0.0:2019
    log default {
        output file /var/log/caddy/caddy.log {
            roll_size 10mb
            roll_keep 5
        }
        format json
    }
}
EOF
fi

if [ ! -f "$managed_fragment" ]; then
    for route_file in "$routes_dir"/site-*.caddy; do
        [ -e "$route_file" ] || continue
        [ "$route_file" = "$managed_fragment" ] && continue
        mv "$route_file" "$legacy_dir/"
    done

    {
        printf '%s\n' '# Caddy UI 2.0 cutover bridge. Replaced by the first successful managed apply.'
        printf 'import %s/site-*.caddy\n' "$legacy_dir"
    } >"$managed_fragment"
fi

temporary="${root_config}.caddy-ui-2.tmp"
awk -v managed="$managed_fragment" '
BEGIN { inserted = 0 }
{
    trimmed = $0
    sub(/^[[:space:]]+/, "", trimmed)
    sub(/[[:space:]]+$/, "", trimmed)
    if (trimmed == "import /etc/caddy/routes/site-*.caddy" ||
        trimmed == "import /etc/caddy/routes/*.caddy" ||
        trimmed == "import " managed) {
        if (!inserted) {
            print "import " managed
            inserted = 1
        }
        next
    }
    print $0
}
END {
    if (!inserted) {
        print ""
        print "import " managed
    }
}
' "$root_config" >"$temporary"
mv "$temporary" "$root_config"

/usr/bin/caddy validate --config "$root_config" --adapter caddyfile
