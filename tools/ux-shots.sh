#!/usr/bin/env bash
# Pictures of gd-ffx's native screens (G5), offscreen under Xvfb with the
# Compatibility renderer, as tools/compare-shot.sh renders (nothing on screen):
# the save sphere's choice (cursor on 0, then 1), the ESC menu, the options, a
# booster's tip and badge, the pause (--selftest-ux at developer save 7), and
# the battle's command menu and target selection (--selftest-battle).
#
#   FFX_GAME_DIR=<game> tools/ux-shots.sh OUT_DIR
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$1"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
cd "$repo/godot"
run() {
    local log="$1"
    shift
    env -u WAYLAND_DISPLAY -u DISPLAY "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1280x720x24" \
        "$godot" --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy --path . -- \
        --no-overlay --documents="$out/docs" "$@" >"$out/$log" 2>&1 || true
    grep -E '^ffx selftest: (PASS|FAIL)' "$out/$log" | cut -c1-200 || echo "no result (see $out/$log)"
}
run ux.log --selftest-ux --devsave=7 --selftest-timeout=200 --shot-dir="$out"
run battle.log --selftest-battle --devsave=7 --battle=3,0,0 --selftest-timeout=200 --shot="$out/battle.png"
rm -rf "$out/docs"
ls "$out"/*.png
