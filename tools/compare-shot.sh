#!/usr/bin/env bash
# Renders Godot's picture of a developer save (offscreen, Xvfb; nothing on
# screen) and compares it with a reference picture of the original's
# renderer at the same spot (tools/compare/diff.py). The reference is a
# screenshot of the player's own game and stays outside git.
#
#   FFX_GAME_DIR=<game> tools/compare-shot.sh REF.png|bmp OUT_DIR [devsave, default 7]
# (any save gives godot.png; the score only means something against a reference of that spot)
# FFX_SHOT_ARGS: more arguments for the check, e.g. "--walk=up:6" (the map check walks
# first) or "--shots=4" (Besaid: four pictures 0.1 s apart, godot.png, godot-1.png, ...)
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
ref="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
out="$2"
save="${3:-7}"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
cd "$repo/godot"
# Besaid (save 7) by its own check; any other save by the map check (--selftest-map)
if [[ "$save" == 7 ]]; then test="--selftest-besaid --no-walk --selftest-timeout=12"
else test="--selftest-map --selftest-timeout=90"; fi
env -u WAYLAND_DISPLAY -u DISPLAY "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1280x720x24" \
    "$godot" --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy --path . -- \
    $test ${FFX_SHOT_ARGS:-} --no-overlay --devsave="$save" \
    --shot="$out/godot.png" --documents="$out/docs" --classic="${FFX_CLASSIC:-$out/classic}" >"$out/godot.log" 2>&1 || true
[[ -f "$out/godot.png" ]] || { echo "compare-shot: no picture (see $out/godot.log)" >&2; exit 1; }
uv run -q --with pillow python -I "$repo/tools/compare/diff.py" "$ref" "$out/godot.png" "$out/side.png"
echo "side by side: $out/side.png"
