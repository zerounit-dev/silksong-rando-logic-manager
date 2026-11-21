#!/bin/sh
set -eu

fail() { echo "Container initialization: $*" >&2; exit 1; }

# Fixed packaging paths. Validate the same state root that the app will use.
[ "${Storage__DataRoot:-}" = /data ] || fail 'Storage__DataRoot must remain /data; mount your state there.'
[ "${LogicManager__ConfigDirectory:-}" = /config ] || fail 'LogicManager__ConfigDirectory must remain /config; mount your configuration there.'
[ "$#" -eq 0 ] || fail 'Configure this image through /config and environment, not entrypoint arguments.'
[ -d /data ] && [ -w /data ] || fail '/data must be a writable directory for the nonroot process.'
[ ! -e /data/.container-seed-in-progress ] && [ ! -L /data/.container-seed-in-progress ] || fail 'Interrupted initialization detected. Preserve /data and follow the deployment recovery guide.'

database=/data/silksong-rando-logic.db
[ ! -L "$database" ] || fail '/data/silksong-rando-logic.db must be a regular file, not a symbolic link.'
[ -e "$database" ] || fail 'Primary database missing. Provision /data/silksong-rando-logic.db before first startup, or restore complete retained state. See the deployment guide.'
[ -f "$database" ] || fail '/data/silksong-rando-logic.db must be a regular file.'

# Validation only: neither database nor scene assets are installed or repaired here.
exec /app/Silksong_Rando_Logic_Manager
