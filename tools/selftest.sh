#!/usr/bin/env bash
# The G0 check, headless (no window, no sound): Godot boots ffx-rust from the
# player's game folder, the game's 30-update loop runs, and a Start press
# through Godot's input takes the opening to the title (docs/running.md).
#
#   FFX_GAME_DIR=<game folder> tools/selftest.sh [--timeout SECONDS]
#
# GODOT: the Godot 4 .NET editor binary (default: ~/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot).
# The game's settings and saves go to a fresh temporary folder, never the
# player's documents folder. Exit status 0 when it passes.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
timeout=300
if [[ "${1:-}" == --timeout ]]; then
    timeout="${2:?seconds}"
fi
: "${FFX_GAME_DIR:?set FFX_GAME_DIR to the game folder (the one with FFX.exe)}"
[[ -f "$repo/rust/target/release/libffx_gd.so" ]] || {
    echo "selftest: build the extension first (tools/build-extension.sh)" >&2
    exit 2
}
cd "$repo/godot"
dotnet build -nologo -v quiet gd-ffx.csproj >/dev/null
# the first run imports the project (and registers the extension)
[[ -f .godot/extension_list.cfg ]] || "$godot" --headless --editor --quit --path . >/dev/null 2>&1 || true
docs="$(mktemp -d -t gd-ffx-selftest.XXXXXX)"
trap 'rm -rf "$docs"' EXIT
set +e +o pipefail
timeout $((timeout + 60)) "$godot" --headless --path . -- --selftest \
    --selftest-timeout="$timeout" --documents="$docs" >"$docs/godot.log" 2>&1
status=$?
grep -E '^ffx|SCRIPT ERROR|ERROR:|panicked' "$docs/godot.log" | cut -c1-300
exit "$status"
