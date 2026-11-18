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
#     an R2-era entry migrated, a pack's profile over the starter one; phase 4:
#     every map added with + map, the seam tool on a synthetic texture with
#     seams, saved with the material's flags, a profile with the maps; with
#     FFX_EDITOR_SHOT=1 also OUT_DIR/materials-*.png, profile-*.png, maps-*.png
#     and seam-*.png);
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
#  8. R7b: 2D rooms and UI art (--remaster-editor-rooms: Rebuild in 3D on the 2D-layer map ptkl14, the
#     scene written with references only, R5's alignment check on it, opened and built: a projected
#     plane a layer, the floor on the walk mesh, the scene camera against the file's and the game's
#     capture, the guides, a user's mesh tagged with a layer, saved without planes, pictures or
#     materials; the UI Art dock: the movies, the save screen's art with thumbnails, a cursor and a
#     portrait assigned, undo/redo, the preview), then the game with what it saved: the rebuilt room on
#     developer save 14 (--selftest-rebuild --rebuild-scene), the UI art on the save screen
#     (--selftest-ui-art --ui-art-file, F8).
#  9. R8: the before/after renders (--remaster-renders under Xvfb on bsil00, ptkl14 and slik08, then
#     again: kept), the status dashboard (--remaster-editor-status: the counts against the files, a
#     thumbnail, the pictures, filters, a budgets fixture), the HTML report and its links.
#  7. R4: the remaster wizard (tools/remaster-wizard-check.sh OUT_DIR: Besaid beach's pass staged,
#     reviewed, accepted, undone and redone in the editor under Xvfb; the game with it; a batch paused
#     and resumed across editor sessions; the classifier's accuracy on its labelled set).
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
# FFX_EDITOR_KEEP_CHECK=1: the check's project folders kept at the end (to run a step again by hand; remove them after)
[[ "${FFX_EDITOR_KEEP_CHECK:-0}" == 1 ]] || trap 'rm -rf "$repo/godot/remaster/check"' EXIT
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
    --remaster-captures="$out/captures" --editor-test-timeout=900)
if [[ -n "${FFX_EDITOR_SHOT:-}" ]]; then
    env -u WAYLAND_DISPLAY -u DISPLAY timeout 1000 "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1600x900x24" \
        "$godot" --editor --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy \
        "${args[@]}" --editor-shot="$out/editor.png" >"$out/editor.log" 2>&1
else
    timeout 1000 "$repo/tools/capped.sh" "$godot" --headless --editor "${args[@]}" >"$out/editor.log" 2>&1
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
# 8. R7b: the rebuilt room and the UI art in the editor (into godot/remaster/check/rooms, in the project
#    so the editor opens it, removed at the end; and OUT_DIR/ui), then the game with them
mkdir -p "$out/ui"
rargs=(--path . -- --remaster-editor-rooms --remaster-rooms=res://remaster/check/rooms --remaster-ui-art="$out/ui/ui.tres"
    --remaster-captures="$out/captures" --editor-test-timeout=400)
timeout 500 "$repo/tools/capped.sh" "$godot" --headless --editor "${rargs[@]}" >"$out/rooms.log" 2>&1
rooms=$?
grep -E '^ffx editortest: (PASS|FAIL)' "$out/rooms.log" | tail -1 | cut -c1-300 || echo "ffx editortest: no result (exit $rooms, see $out/rooms.log)"
[[ $rooms -eq 0 ]] || exit 1
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-rebuild --rebuild-scene=res://remaster/check/rooms/ptkl14.tscn \
    --devsave=14 --documents="$out/docs" --selftest-timeout=150 >"$out/rooms-game.log" 2>&1
rg=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/rooms-game.log" | tail -1 | cut -c1-300 || echo "ffx selftest: no result (exit $rg, see $out/rooms-game.log)"
[[ $rg -eq 0 ]] || exit 1
timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-ui-art --ui-art-file="$out/ui/ui.tres" --ui-art-expect="$out/ui/expect.txt" \
    --devsave=7 --documents="$out/docs" --selftest-timeout=150 >"$out/ui-art-game.log" 2>&1
ug=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/ui-art-game.log" | tail -1 | cut -c1-300 || echo "ffx selftest: no result (exit $ug, see $out/ui-art-game.log)"
[[ $ug -eq 0 ]] || exit 1
# 9. R8: the before/after renders and the status dashboard, over what the steps above saved. The renders
#    batch (--remaster-renders, the editor under Xvfb: a renderer is needed) on bsil00 (the check's edits,
#    textures, materials and lighting), ptkl14 (step 8's rebuilt room) and slik08 (untouched) into
#    OUT_DIR/renders, run again (every map kept: resumable); the dashboard (--remaster-editor-status,
#    headless) against the files and a budgets table made by tools/compare/budget.py from synthetic
#    records (no game data); then the HTML report (tools/remaster-report.sh) and its links.
status_args=(--remaster-maps="$out/maps" --remaster-textures="$tex" --remaster-looks="$out/looks" --remaster-captures="$out/captures"
    --remaster-effect-scenes="$out/media/effect" --remaster-audio="$out/media/audio/audio.tres" --remaster-movies="$out/media/movies/movies.tres"
    --remaster-rooms=res://remaster/check/rooms --remaster-ui-art="$out/ui/ui.tres" --remaster-characters="$out/characters"
    --renders-dir="$out/renders" --remaster-budgets="$out/budgets")
for pass in 1 2; do
    env -u WAYLAND_DISPLAY -u DISPLAY timeout 900 "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1600x900x24" \
        "$godot" --editor --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy --path . -- \
        --remaster-renders --maps=bsil00,ptkl14,slik08 "${status_args[@]}" >"$out/renders-$pass.log" 2>&1
    rr=$?
    grep -E '^ffx remaster: renders: [a-z0-9]+: ' "$out/renders-$pass.log" | cut -c1-260
    grep -E '^ffx renders: (DONE|FAIL)' "$out/renders-$pass.log" | tail -1 || echo "ffx renders: no result (exit $rr, see $out/renders-$pass.log)"
    [[ $rr -eq 0 ]] || exit 1
done
grep -q '^ffx renders: DONE 0 drawn, 3 kept' "$out/renders-2.log" || { echo "the second renders run drew maps again (see $out/renders-2.log)"; exit 1; }
mkdir -p "$out/budgets" "$out/budget-records"
python3 -I - "$out/budget-records" <<'PY'
import json, sys
base = {"key": "bsil00", "level": "/map/bsil/bsil00/", "gi": "None", "adapter": "check fixture (synthetic)", "width": 1280, "height": 800,
        "gpu_ms": 0, "gpu_p99_ms": 0, "frame_p99_ms": 9.0, "texture_mib": 400.0, "video_mib": 900.0, "draw_calls": 600}
recs = [dict(base, preset="deck", path="classic", frame_ms=5.0), dict(base, preset="desktop", path="remastered", width=1920, height=1080, frame_ms=21.0)]
for i, r in enumerate(recs):
    json.dump(r, open(f"{sys.argv[1]}/r{i}.json", "w"))
PY
python3 -I "$repo/tools/compare/budget.py" "$out/budgets/fixture.json" "$out"/budget-records/*.json >"$out/budget-fixture.txt" 2>&1
[[ $? -eq 1 ]] || { echo "budget.py: the fixture's desktop run (21 ms) is not over budget (see $out/budget-fixture.txt)"; exit 1; }
timeout 400 "$repo/tools/capped.sh" "$godot" --headless --editor --path . -- --remaster-editor-status "${status_args[@]}" \
    --status-report="$out/report-editor" >"$out/status.log" 2>&1
st=$?
grep -E '^ffx editortest: (PASS|FAIL)' "$out/status.log" | tail -1 | cut -c1-900 || echo "ffx editortest: no result (exit $st, see $out/status.log)"
[[ $st -eq 0 ]] || exit 1
"$repo/tools/remaster-report.sh" "$out/report" -- "${status_args[@]}" || exit 1
# 7. R4: the remaster wizard (tools/remaster-wizard-check.sh: the editor under Xvfb, the game with what
#    it accepted, a batch paused and resumed across editor sessions, the classifier's accuracy)
"$repo/tools/remaster-wizard-check.sh" "$out" || exit 1
echo "remaster-editor-check: PASS (logs and files in $out)"
