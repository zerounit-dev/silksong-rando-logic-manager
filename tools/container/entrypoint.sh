#!/bin/sh
set -eu

fail() { echo "Container initialization: $*" >&2; exit 1; }

# Fixed packaging paths. Do not silently seed one root then run against another.
[ "${Storage__DataRoot:-}" = /data ] || fail 'Storage__DataRoot must remain /data; mount your state there.'
[ "${LogicManager__ConfigDirectory:-}" = /config ] || fail 'LogicManager__ConfigDirectory must remain /config; mount your configuration there.'
[ "$#" -eq 0 ] || fail 'Configure this image through /config and environment, not entrypoint arguments.'
[ -d /data ] && [ -w /data ] || fail '/data must be a writable directory for the nonroot process.'
[ ! -e /data/.container-seed-in-progress ] || fail 'Interrupted initialization detected. Preserve /data and follow the deployment recovery guide.'

if [ -e /data/silksong-rando-logic.db ]; then
    [ -f /data/silksong-rando-logic.db ] && [ ! -L /data/silksong-rando-logic.db ] || fail 'Existing primary database is not a regular file.'
    # Existing state is authoritative, including an invalid/empty DB. Never repair it.
else
    entries=$(find /data -mindepth 1 -maxdepth 1 -print -quit) || fail 'Could not enumerate /data; verify directory read/search permissions before initialization.'
    [ -z "$entries" ] || fail '/data is nonempty but has no primary database. Restore complete state or select a genuinely empty mount.'
    mkdir /data/.container-seed-in-progress || fail 'Could not acquire first-install initialization marker.'
    # Preserve this marker on any failure/interruption. A partial copy never becomes
    # an apparently complete new installation on the next start.
    cp -R /opt/logic-manager-seed/. /data/.container-seed-in-progress/
    for item in /data/.container-seed-in-progress/*; do
        [ -e "$item" ] || continue
        mv "$item" /data/
    done
    rmdir /data/.container-seed-in-progress
fi

exec /app/Silksong_Rando_Logic_Manager
