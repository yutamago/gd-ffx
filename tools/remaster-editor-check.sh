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
#     then R2: the where-used index, a texture replaced by a generated test image,
#     a material generated for another (into godot/remaster/check/textures: in
#     the project so Godot imports the PNGs; gitignored, removed at the end),
#     Besaid's render path set to remastered, the 3D view's toggle, the batch
#     with its review, each edit undone and redone;
#  3. the game with the editor's textures (--selftest-materials --textures-file):
#     the replacement drawn classic, the material remastered, F7;
#     then R3: the game's lighting as nodes (the key light from a stand-in look in
#     OUT_DIR/looks), the key light turned, the exposure and the fog changed, a
#     volume added, undo/redo, lighting.tres saved (the same twice), opened again;
#     the game applies the overrides and the lighting (drawn remastered: the key
#     light as turned, the exposure, the volume);
#     then R2b: the Materials tab (three textures queued, every step, a slider
#     dragged, the latency at 512 and 1024 px, undo/redo, a map imported and one
#     skipped, the next texture with the settings kept, saved; with
#     FFX_EDITOR_SHOT=1 also OUT_DIR/materials-*.png);
#  4. the R1a example's hand-written overrides opened in the editor, shown as
#     edits, and saved back with the same meaning (--remaster-editor-roundtrip);
#     R5: the alignment check on its additions and replacement, and a misplaced
#     test box flagged.
#  5. R6: the character tools (--remaster-editor-character: the character list, c001 opened
#     with its motions played and scrubbed, the original stand-in humanoid assigned as its remake,
#     the automatic mapping, one bone mapped by hand, save, undo/redo, opened again; into
#     OUT_DIR/characters), then the game with that scene and rig (--selftest-character
#     --character-scene: the stand-in retargeted on Tidus, walking).
#  6. R7: the game's effect recordings, then the Media dock (--remaster-editor-media: the Effects,
#     Audio and Movies tabs: list, open, assign, undo/redo, read back; into OUT_DIR/media), then the
#     game with the editor's effect scene, sounds and movie (see step 6 below).
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
args=(--path . -- --remaster-editor-test --remaster-maps="$out/maps" --remaster-textures="$tex" --remaster-looks="$out/looks"
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
# 5. R6: the character tools in the editor (the list, c001 opened with its motions, the stand-in
#    humanoid assigned, the automatic mapping, one bone mapped by hand, save, undo/redo, opened
#    again: into OUT_DIR/characters), then the game with the scene and rig the editor saved
mkdir -p "$out/characters"
cargs=(--path . -- --remaster-editor-character --remaster-characters="$out/characters")
if [[ -n "${FFX_EDITOR_SHOT:-}" ]]; then
    env -u WAYLAND_DISPLAY -u DISPLAY timeout 400 "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1600x900x24" \
        "$godot" --editor --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy \
        "${cargs[@]}" --editor-shot="$out/editor-character.png" >"$out/characters.log" 2>&1
else
    timeout 400 "$repo/tools/capped.sh" "$godot" --headless --editor "${cargs[@]}" >"$out/characters.log" 2>&1
fi
chr=$?
grep -E '^ffx editortest: (PASS|FAIL)' "$out/characters.log" | tail -1 || echo "ffx editortest: no result (exit $chr, see $out/characters.log)"
[[ $chr -eq 0 ]] || exit 1
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-character \
    --character-scene="$out/characters/c001.tscn" --character-expect="$out/characters/expect.json" \
    --devsave=7 --documents="$out/docs" --selftest-timeout=200 >"$out/character-game.log" 2>&1
cg=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/character-game.log" | tail -1 || echo "ffx selftest: no result (exit $cg, see $out/character-game.log)"
[[ $cg -eq 0 ]] || exit 1
# 6. R7: effects, audio and movies. The game records Besaid's effects (--selftest-effects: the
#    example remake on the save sphere's glow, F8, the recording) into OUT_DIR/effects; a test
#    video is made (tools/make-test-movie.sh, Xvfb); the editor (--remaster-editor-media) lists the
#    recordings, opens bsil00-fp15 (its replay, scrubbed), assigns the example as its remake, the
#    battle music and the sea effect stand-ins, the test video to LCI, each undone and redone,
#    read back (into OUT_DIR/media); then the game with what it saved: the editor's effect scene
#    on the save sphere, its music in the deck battle, its movie on developer save 22.
mkdir -p "$out/media"
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-effects --remaster-effects="$out/effects" \
    --devsave=7 --documents="$out/docs" --selftest-timeout=150 >"$out/effects.log" 2>&1
fx=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/effects.log" | tail -1 | cut -c1-300 || echo "ffx selftest: no result (exit $fx, see $out/effects.log)"
[[ $fx -eq 0 ]] || exit 1
"$repo/tools/make-test-movie.sh" "$out/media/test.ogv" 40 >"$out/movie-make.log" 2>&1 || { echo "make-test-movie failed (see $out/movie-make.log)"; exit 1; }
margs=(--path . -- --remaster-editor-media --remaster-effects="$out/effects" --remaster-effect-scenes="$out/media/effect"
    --remaster-audio="$out/media/audio/audio.tres" --remaster-movies="$out/media/movies/movies.tres" --media-video="$out/media/test.ogv")
timeout 400 "$repo/tools/capped.sh" "$godot" --headless --editor "${margs[@]}" >"$out/media.log" 2>&1
med=$?
grep -E '^ffx editortest: (PASS|FAIL)' "$out/media.log" | tail -1 || echo "ffx editortest: no result (exit $med, see $out/media.log)"
[[ $med -eq 0 ]] || exit 1
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-effects --effect-scene="$out/media/effect/bsil00-fp15.tscn" \
    --remaster-effects="$out/effects-game" --devsave=7 --documents="$out/docs" --selftest-timeout=150 >"$out/media-effects.log" 2>&1
mfx=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/media-effects.log" | tail -1 | cut -c1-300 || echo "ffx selftest: no result (exit $mfx, see $out/media-effects.log)"
[[ $mfx -eq 0 ]] || exit 1
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-audio --audio-file="$out/media/audio/audio.tres" \
    --devsave=7 --documents="$out/docs" --selftest-timeout=150 >"$out/media-audio.log" 2>&1
mau=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/media-audio.log" | tail -1 | cut -c1-300 || echo "ffx selftest: no result (exit $mau, see $out/media-audio.log)"
[[ $mau -eq 0 ]] || exit 1
timeout 400 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-movie --movies-file="$out/media/movies/movies.tres" \
    --devsave=22 --documents="$out/docs" --selftest-timeout=200 >"$out/media-movie.log" 2>&1
mmv=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/media-movie.log" | tail -1 | cut -c1-300 || echo "ffx selftest: no result (exit $mmv, see $out/media-movie.log)"
[[ $mmv -eq 0 ]] || exit 1
echo "remaster-editor-check: PASS (logs and files in $out)"
