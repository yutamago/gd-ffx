#!/usr/bin/env bash
# Runs a heavy command (builds, tests, game runs) inside the shared memory cap,
# the systemd user slice `gdffx.slice` (MemoryMax 24G, swap 2G: see
# docs/building.md, "Memory"), with cargo limited to 8 jobs. All heavy work on
# the machine shares that one cap, so several at once cannot exhaust memory
# (WSL crashes when memory and swap run out).
#
#   tools/capped.sh cargo build --release ...
set -euo pipefail
if ! systemctl --user is-active -q gdffx.slice 2>/dev/null; then
    systemctl --user start gdffx.slice 2>/dev/null || {
        echo "capped.sh: no gdffx.slice (docs/building.md, \"Memory\")" >&2
        exit 2
    }
fi
export CARGO_BUILD_JOBS="${CARGO_BUILD_JOBS:-8}"
exec systemd-run --user --scope -q --slice=gdffx.slice -p OOMPolicy=continue "$@"
