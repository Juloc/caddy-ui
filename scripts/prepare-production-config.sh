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

python3 - "$root_config" "$managed_fragment" <<'PY'
from pathlib import Path
import sys

root = Path(sys.argv[1])
managed = sys.argv[2]
lines = root.read_text(encoding="utf-8").splitlines()
legacy_imports = {
    "import /etc/caddy/routes/site-*.caddy",
    "import /etc/caddy/routes/*.caddy",
}
result = []
inserted = False
for line in lines:
    stripped = line.strip()
    if stripped == f"import {managed}":
        if not inserted:
            result.append(f"import {managed}")
            inserted = True
        continue
    if stripped in legacy_imports:
        if not inserted:
            result.append(f"import {managed}")
            inserted = True
        continue
    result.append(line)
if not inserted:
    if result and result[-1] != "":
        result.append("")
    result.append(f"import {managed}")
root.write_text("\n".join(result) + "\n", encoding="utf-8")
PY

/usr/bin/caddy validate --config "$root_config" --adapter caddyfile
