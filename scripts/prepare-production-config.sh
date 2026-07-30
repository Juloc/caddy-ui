#!/bin/sh
set -eu

root_config="${CADDY_UI_CADDY_ROOT_CONFIG:-/etc/caddy/Caddyfile}"
routes_dir="${CADDY_UI_ROUTES_DIR:-/etc/caddy/routes}"
managed_fragment="${CADDY_UI_MANAGED_ROUTES_PATH:-${routes_dir}/site-managed-routes.caddy}"
blocklist_fragment="${CADDY_UI_BLOCKLIST_PATH:-${routes_dir}/site-security-blocks.caddy}"
legacy_dir="${CADDY_UI_LEGACY_ROUTES_DIR:-${routes_dir}/legacy-dotnet-cutover}"
acme_email="${ACME_EMAIL:-}"

mkdir -p "$routes_dir" "$legacy_dir"

if [ ! -f "$root_config" ]; then
    cat >"$root_config" <<'EOF'
{
EOF
    if [ -n "$acme_email" ]; then
        printf '%s\n' '    email {$ACME_EMAIL}' >>"$root_config"
    fi
    cat >>"$root_config" <<'EOF'
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
    moved_legacy_routes=0
    for route_file in "$routes_dir"/site-*.caddy; do
        [ -e "$route_file" ] || continue
        [ "$route_file" = "$managed_fragment" ] && continue
        [ "$route_file" = "$blocklist_fragment" ] && continue
        mv "$route_file" "$legacy_dir/"
        moved_legacy_routes=1
    done

    printf '%s\n' '# Caddy UI 2.0 managed routes.' >"$managed_fragment"
    if [ "$moved_legacy_routes" = "1" ]; then
        printf '%s\n' '# Cutover bridge. Replaced by the first successful managed apply.' >>"$managed_fragment"
        printf 'import %s/site-*.caddy\n' "$legacy_dir" >>"$managed_fragment"
    fi
fi

if [ ! -f "$blocklist_fragment" ]; then
    printf '%s\n' '# Managed IP block feed: address|blocked-until|reason' >"$blocklist_fragment"
fi

email_required=0
if [ -n "$acme_email" ]; then
    email_required=1
fi

temporary="${root_config}.caddy-ui-2.tmp"
awk \
    -v managed="$managed_fragment" \
    -v email_required="$email_required" '
BEGIN {
    inserted = 0
    global_seen = 0
    in_global = 0
    email_written = 0
}
{
    trimmed = $0
    sub(/^[[:space:]]+/, "", trimmed)
    sub(/[[:space:]]+$/, "", trimmed)

    if (!global_seen && trimmed == "{") {
        global_seen = 1
        in_global = 1
        print $0
        if (email_required == "1") {
            print "    email {$ACME_EMAIL}"
            email_written = 1
        }
        next
    }

    if (in_global && trimmed == "}") {
        in_global = 0
        print $0
        next
    }

    if (in_global && trimmed ~ /^email([[:space:]]|$)/) {
        if (email_required == "1") {
            if (!email_written) {
                print "    email {$ACME_EMAIL}"
                email_written = 1
            }
            next
        }

        if (trimmed == "email {$ACME_EMAIL}" || trimmed == "email") {
            next
        }
    }

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
