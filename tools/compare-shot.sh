#!/usr/bin/env bash
# Renders Godot's picture of a developer save (offscreen, Xvfb; nothing on
# screen) and compares it with a reference picture of the original's
# renderer at the same spot (tools/compare/diff.py). The reference is a
# screenshot of the player's own game and stays outside git.
#
#   FFX_GAME_DIR=<game> tools/compare-shot.sh REF.png|bmp OUT_DIR [devsave, default 7]
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
ref="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
out="$2"
save="${3:-7}"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
cd "$repo/godot"
env -u WAYLAND_DISPLAY -u DISPLAY "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1280x720x24" \
    "$godot" --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy --path . -- \
    --selftest-besaid --no-walk --selftest-timeout=12 --no-overlay --devsave="$save" \
    --shot="$out/godot.png" --documents="$out/docs" --classic="${FFX_CLASSIC:-$out/classic}" >"$out/godot.log" 2>&1 || true
[[ -f "$out/godot.png" ]] || { echo "compare-shot: no picture (see $out/godot.log)" >&2; exit 1; }
uv run -q --with pillow python -I "$repo/tools/compare/diff.py" "$ref" "$out/godot.png" "$out/side.png"
echo "side by side: $out/side.png"
