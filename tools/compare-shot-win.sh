#!/usr/bin/env bash
# The Windows export's picture of a developer save on the machine's own GPU
# (D3D12 or Vulkan, Forward+), compared with a reference picture of the
# original, as tools/compare-shot.sh does for the Linux build under Xvfb.
# WSL only: runs build/windows/gd-ffx.console.exe (tools/export.sh windows
# first) with its window placed off the desktop (it renders, but is not seen;
# a taskbar entry may show while it runs). The game folder and the pictures
# stay on the WSL side, reached through \\wsl.localhost.
#
#   FFX_GAME_DIR=<game> tools/compare-shot-win.sh REF.png|bmp OUT_DIR [devsave, default 7] [d3d12|vulkan]
# FFX_SHOT_ARGS: more arguments for the check, as tools/compare-shot.sh.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
ref="$1"; out="$2"; save="${3:-7}"; driver="${4:-d3d12}"
game="${FFX_GAME_DIR:?FFX_GAME_DIR: the game folder}"
exe="$repo/build/windows/gd-ffx.console.exe"
[[ -f "$exe" ]] || { echo "compare-shot-win: no $exe (tools/export.sh windows)" >&2; exit 1; }
chmod +x "$exe" "$repo/build/windows/gd-ffx.exe"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
rm -f "$out/godot.png"
win() { wslpath -w "$1"; }
mkdir -p "$out/docs"
if [[ "$save" == 7 ]]; then test="--selftest-besaid --no-walk --selftest-timeout=12"
else test="--selftest-map --selftest-timeout=90"; fi
# shellcheck disable=SC2086
timeout 300 "$exe" --position -30000,-30000 --rendering-driver "$driver" -- \
    --game-dir="$(win "$(readlink -f "$game")")" --documents="$(win "$out/docs")" \
    $test ${FFX_SHOT_ARGS:-} --no-overlay --devsave="$save" --shot="$(win "$out/godot.png")" \
    2>&1 | tr -d '\r' >"$out/godot.log" || true
grep -E 'Forward|Compatibility|Using Device' "$out/godot.log" | head -1 || true
[[ -f "$out/godot.png" ]] || { echo "compare-shot-win: no picture (see $out/godot.log)" >&2; exit 1; }
uv run -q --with pillow python -I "$repo/tools/compare/diff.py" "$ref" "$out/godot.png" "$out/side.png"
echo "side by side: $out/side.png"
