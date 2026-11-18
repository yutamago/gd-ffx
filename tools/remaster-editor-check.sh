#!/usr/bin/env bash
# The remaster editor's check (docs/remaster-editor.md, "Checks"), both halves,
# through tools/capped.sh, nothing on screen:
#  1. the editor, headless (or under Xvfb with FFX_EDITOR_SHOT=1: a picture of
#     its 3D view, OUT_DIR/editor.png), drives the plugin: the catalogue, Besaid
#     beach opened, a move, a hidden segment, a removed instance, a replaced
#     part, a box added at a part, undo/redo, save (into OUT_DIR/maps, not the
#     project), the map opened again, the status, the play plan;
#  2. the game on developer save 7 with those saved overrides
#     (--selftest-saved-overrides): every edit where the editor showed it;
#     then R2: the where-used index, a texture replaced by a generated test image
#     (into godot/remaster/check/textures: in the project so Godot imports the
#     PNGs; gitignored, removed at the end), Besaid's render path set to
#     remastered, the 3D view's toggle, two materials made by a starter profile
#     (R2b phase 3's run; R2's Generate and batch went), each edit undone and
#     redone;
#  3. the game with the editor's textures (--selftest-materials --textures-file):
#     the replacement drawn classic, the material remastered, F7;
#     then R3: the game's lighting as nodes (the key light from a stand-in look in
#     OUT_DIR/looks), the key light turned, the exposure and the fog changed, a
#     volume added, undo/redo, lighting.tres saved (the same twice), opened again;
#     the game applies the overrides and the lighting (drawn remastered: the key
#     light as turned, the exposure, the volume);
#     then R2b: the Materials tab (three textures queued, every step, a slider
#     dragged, the latency at 512 and 1024 px, undo/redo, a map imported and one
#     skipped, the next texture with the settings kept, saved; phase 2's bands,
#     colour samples, painted light, occlusion; phase 3: the starter profiles,
#     a profile saved (into godot/remaster/check/profiles, gitignored, removed),
#     loaded, an exception pinned and cleared, Apply to queue (cancelled once,
#     then whole), undo/redo, the profile changed and the textures made again,
#     an R2-era entry migrated, a pack's profile over the starter one; with
#     FFX_EDITOR_SHOT=1 also OUT_DIR/materials-*.png and profile-*.png);
#  4. the R1a example's hand-written overrides opened in the editor, shown as
#     edits, and saved back with the same meaning (--remaster-editor-roundtrip);
#     R5: the alignment check on its additions and replacement, and a misplaced
#     test box flagged.
#  R5 (before 1): the game's --selftest-alignment on developer save 7 (the game
#  data against the running game, the alignment check) leaves its capture of
#  Besaid beach in OUT_DIR/captures, which every editor run reads: the editor
#  check then shows the game's data on the beach (the walk mesh, triggers, the
#  exit named, entrances, cameras, the birds where the game had them), checks the
#  alignment with a misplaced box, and opens the 2D-layer map ptkl14 (its walk
#  mesh, triggers, entrances, fixed camera against the game's: the map check on
#  developer save 14 captures it).
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
tex="res://remaster/check/textures"
rm -rf "$repo/godot/remaster/check"
trap 'rm -rf "$repo/godot/remaster/check"' EXIT
cd "$repo/godot"
# R5: the game's data in the running game; its capture for the editor runs below
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-alignment --remaster-captures="$out/captures" \
    --devsave=7 --documents="$out/docs" --selftest-timeout=150 >"$out/alignment.log" 2>&1
align=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/alignment.log" | tail -1 || echo "ffx selftest: no result (exit $align, see $out/alignment.log)"
[[ $align -eq 0 ]] || exit 1
# and the game's camera on the 2D-layer map ptkl14 (developer save 14), for its fixed camera
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-map --remaster-captures="$out/captures" \
    --devsave=14 --documents="$out/docs" --selftest-timeout=90 >"$out/map14.log" 2>&1
map14=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/map14.log" | tail -1 | cut -c1-200 || echo "ffx selftest: no result (exit $map14, see $out/map14.log)"
[[ $map14 -eq 0 ]] || exit 1
args=(--path . -- --remaster-editor-test --remaster-maps="$out/maps" --remaster-textures="$tex" --remaster-profiles="res://remaster/check/profiles" --remaster-looks="$out/looks"
    --remaster-captures="$out/captures")
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
    --lighting-file="$out/maps/bsil00/lighting.tres" --lighting-expect="$out/maps/lighting-expect.json" --remaster-looks="$out/looks" \
    --devsave=7 --documents="$out/docs" --selftest-timeout=150 >"$out/game.log" 2>&1
game=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/game.log" | tail -1 || echo "ffx selftest: no result (exit $game, see $out/game.log)"
[[ $game -eq 0 ]] || exit 1
# 3. the game with the editor's textures and materials
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-materials \
    --textures-file="$tex/textures.tres" --textures-expect="$out/maps/textures-expect.json" \
    --devsave=7 --documents="$out/docs" --selftest-timeout=200 >"$out/materials.log" 2>&1
mat=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/materials.log" | tail -1 || echo "ffx selftest: no result (exit $mat, see $out/materials.log)"
[[ $mat -eq 0 ]] || exit 1
# 4. the R1a example's hand-written set opened in the editor and saved back unchanged
mkdir -p "$out/roundtrip/bsil00"
cp "$repo/godot/remaster/examples/bsil00/overrides.tres" "$repo/godot/remaster/examples/bsil00/additions.tscn" "$out/roundtrip/bsil00/"
timeout 400 "$repo/tools/capped.sh" "$godot" --headless --editor --path . -- --remaster-editor-test --remaster-editor-roundtrip \
    --remaster-maps="$out/roundtrip" --remaster-captures="$out/captures" >"$out/roundtrip.log" 2>&1
rt=$?
grep -E '^ffx editortest: (PASS|FAIL)' "$out/roundtrip.log" | tail -1 || echo "ffx editortest: no result (exit $rt, see $out/roundtrip.log)"
[[ $rt -eq 0 ]] || exit 1
echo "remaster-editor-check: PASS (logs and files in $out)"
