#!/usr/bin/env bash
# The Windows export's picture of a developer save on the machine's own GPU
# (D3D12 or Vulkan, Forward+), compared with a reference picture of the
# original, as tools/compare-shot.sh does for the Linux build under Xvfb.
# WSL only: runs build/windows/gd-ffx.console.exe (tools/export.sh windows
# first) silently (--audio-driver Dummy), its window behind every other window
# (--background-window: no focus, sent to the bottom; it still renders). The game folder and the pictures
# stay on the WSL side, reached through \\wsl.localhost.
#
#   FFX_GAME_DIR=<game> tools/compare-shot-win.sh REF.png|bmp OUT_DIR [devsave, default 7] [vulkan|d3d12] (default vulkan, the project's)
# FFX_SHOT_ARGS: more arguments for the check, as tools/compare-shot.sh.
# FFX_SHOT_TEST: the check to run instead, as tools/compare-shot.sh (e.g.
# "--selftest-materials --selftest-timeout=200": godot.png and godot-remastered.png;
# "--selftest-lighting --selftest-timeout=400": godot.png classic, godot-remastered.png and
# godot-pbr.png remastered, each measured against the classic one by tools/compare/colour.py)
# REF "-": no reference (the pictures and their colour check only)
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
ref="$1"; out="$2"; save="${3:-7}"; driver="${4:-vulkan}"
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
test="${FFX_SHOT_TEST:-$test}"
# shellcheck disable=SC2086
timeout 300 "$exe" --audio-driver Dummy --rendering-driver "$driver" -- \
    --game-dir="$(win "$(readlink -f "$game")")" --documents="$(win "$out/docs")" \
    --background-window $test ${FFX_SHOT_ARGS:-} --no-overlay --devsave="$save" --shot="$(win "$out/godot.png")" \
    2>&1 | tr -d '\r' >"$out/godot.log" || true
grep -E 'Forward|Compatibility|Using Device' "$out/godot.log" | head -1 || true
[[ -f "$out/godot.png" ]] || { echo "compare-shot-win: no picture (see $out/godot.log)" >&2; exit 1; }
# the remastered pictures' colour balance against the classic one (R3)
status=0
for r in remastered pbr; do
    if [[ -f "$out/godot-$r.png" ]]; then
        echo "colour, $r against classic:"
        uv run -q --with pillow python -I "$repo/tools/compare/colour.py" "$out/godot.png" "$out/godot-$r.png" || status=1
    fi
done
grep -E 'ffx selftest: (PASS|FAIL)' "$out/godot.log" | tail -1 | cut -c1-400 || true
[[ "$ref" == - ]] && exit $status
uv run -q --with pillow python -I "$repo/tools/compare/diff.py" "$ref" "$out/godot.png" "$out/side.png"
echo "side by side: $out/side.png"
exit $status
