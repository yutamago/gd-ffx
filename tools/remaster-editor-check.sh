#!/usr/bin/env bash
# The remaster editor's check (docs/remaster-editor.md, "Checks"), both halves,
# through tools/capped.sh, nothing on screen:
#  1. the editor, headless (or under Xvfb with FFX_EDITOR_SHOT=1: a picture of
#     its 3D view, OUT_DIR/editor.png), drives the plugin: the catalogue, Besaid
#     beach opened, a move, a hidden segment, a removed instance, a replaced
#     part, a box added at a part, undo/redo, save (into OUT_DIR/maps, not the
#     project), the map opened again, the status, the play plan;
#  2. the game on developer save 7 with those saved overrides
#     (--selftest-saved-overrides): every edit where the editor showed it.
#
#   FFX_GAME_DIR=<game> tools/remaster-editor-check.sh [OUT_DIR]
#
# OUT_DIR (default /tmp/gd-ffx-editor-check) keeps the logs, the saved files and
# the picture; remove it when done.
set -u
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
out="${1:-/tmp/gd-ffx-editor-check}"
rm -rf "$out"
mkdir -p "$out/maps"
out="$(cd "$out" && pwd)"
cd "$repo/godot"
args=(--path . -- --remaster-editor-test --remaster-maps="$out/maps")
if [[ -n "${FFX_EDITOR_SHOT:-}" ]]; then
    env -u WAYLAND_DISPLAY -u DISPLAY timeout 400 "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1600x900x24" \
        "$godot" --editor --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy \
        "${args[@]}" --editor-shot="$out/editor.png" >"$out/editor.log" 2>&1
else
    timeout 400 "$repo/tools/capped.sh" "$godot" --headless --editor "${args[@]}" >"$out/editor.log" 2>&1
fi
editor=$?
grep -E '^ffx editortest: (PASS|FAIL)' "$out/editor.log" | tail -1 || echo "ffx editortest: no result (exit $editor, see $out/editor.log)"
[[ $editor -eq 0 ]] || exit 1
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-saved-overrides \
    --overrides-file="$out/maps/bsil00/overrides.tres" --overrides-expect="$out/maps/expect.json" \
    --devsave=7 --documents="$out/docs" --selftest-timeout=150 >"$out/game.log" 2>&1
game=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/game.log" | tail -1 || echo "ffx selftest: no result (exit $game, see $out/game.log)"
[[ $game -eq 0 ]] || exit 1
echo "remaster-editor-check: PASS (logs and files in $out)"
