#!/usr/bin/env bash
# R4, the remaster wizard's check (docs/remaster-editor.md, "Checks"; step 7 of
# tools/remaster-editor-check.sh, or alone), through tools/capped.sh, nothing on screen:
#  a. the editor under Xvfb (the review's pictures need a renderer; headless without xvfb-run):
#     --remaster-editor-wizard: Besaid beach from the map browser's selection (profiles chosen
#     with scores and reasons, materials made, lighting staged, before/after pictures, the colour
#     check; the project untouched), the review (a texture rejected and a texture made with
#     another profile, each undone and redone), the map accepted (one undo step, undone, redone),
#     a batch of three maps with a 2D-layer one, paused after the first and resumed;
#  b. the game on developer save 7 with what the editor accepted (--selftest-wizard
#     --wizard-expect: every material drawn, the rejected one classic, the lighting file in
#     force), under Xvfb with pictures and the colour check (tools/compare-shot.sh): OUT_DIR/wizard-game;
#  c. a headless batch paused in one editor session and resumed in the next (--remaster-wizard
#     --wizard-pause-after=1, then --wizard-resume);
#  d. the classifier's accuracy on the hand-labelled set (--selftest-wizard-classifier).
#
#   FFX_GAME_DIR=<game> tools/remaster-wizard-check.sh [OUT_DIR]
#
# OUT_DIR (default /tmp/gd-ffx-wizard-check) keeps the logs, the wizard's state and pictures
# (the player's game: keep them out of git); remove it when done. The made maps go to
# godot/remaster/check/wizard-textures (in the project so Godot imports them; gitignored, removed).
set -u
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
out="${1:-/tmp/gd-ffx-wizard-check}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
tex="res://remaster/check/wizard-textures"
own_check=0
if [[ ! -d "$repo/godot/remaster/check" ]]; then
    own_check=1
    trap 'rm -rf "$repo/godot/remaster/check/wizard-textures"; rmdir "$repo/godot/remaster/check" 2>/dev/null' EXIT
fi
rm -rf "$repo/godot/remaster/check/wizard-textures" "$out/wizard" "$out/wizard-batch" "$out/wizard-game"
cd "$repo/godot"
# the game's capture of Besaid beach (the review's camera), unless the editor check made it
if [[ ! -f "$out/captures/bsil00.json" ]]; then
    timeout 300 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-alignment --remaster-captures="$out/captures" \
        --devsave=7 --documents="$out/docs" --selftest-timeout=150 >"$out/wizard-capture.log" 2>&1
    rm -rf "$out/docs"
fi
# a. the editor
wargs=(--path . -- --remaster-editor-wizard --remaster-wizard-dir="$out/wizard" --remaster-maps="$out/wizard/maps" --remaster-textures="$tex"
    --remaster-looks="$out/looks" --remaster-captures="$out/captures")
if command -v xvfb-run >/dev/null; then
    env -u WAYLAND_DISPLAY -u DISPLAY timeout 1500 "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1600x900x24" \
        "$godot" --editor --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy "${wargs[@]}" >"$out/wizard.log" 2>&1
else
    timeout 1500 "$repo/tools/capped.sh" "$godot" --headless --editor "${wargs[@]}" >"$out/wizard.log" 2>&1
fi
wiz=$?
grep -E '^ffx editortest: (PASS|FAIL)' "$out/wizard.log" | tail -1 | cut -c1-600 || echo "ffx editortest: no result (exit $wiz, see $out/wizard.log)"
[[ $wiz -eq 0 ]] || exit 1
# b. the game with the editor's accepted pass (pictures and the colour check under Xvfb)
FFX_SHOT_TEST="--selftest-wizard --wizard-expect=$out/wizard/wizard-expect.json --selftest-timeout=300" \
    timeout 600 "$repo/tools/compare-shot.sh" - "$out/wizard-game" >"$out/wizard-game.txt" 2>&1
grep -E '^ffx selftest: (PASS|FAIL)' "$out/wizard-game/godot.log" | tail -1 | cut -c1-600 || echo "ffx selftest: no result (see $out/wizard-game/godot.log)"
grep -q '^ffx selftest: PASS' "$out/wizard-game/godot.log" || exit 1
# c. a batch over three maps (a 2D-layer one among them) paused in one editor session, resumed in the next
bargs=(--path . -- --remaster-wizard --remaster-wizard-dir="$out/wizard-batch" --remaster-maps="$out/wizard-batch/maps" --remaster-textures="$tex")
timeout 600 "$repo/tools/capped.sh" "$godot" --headless --editor "${bargs[@]}" --maps=slik08,ptkl14,mtgz03 --wizard-pause-after=1 >"$out/wizard-batch-1.log" 2>&1
grep -E '^ffx wizard: (DONE|FAIL)' "$out/wizard-batch-1.log" | tail -1 | cut -c1-400
grep -q '^ffx wizard: DONE paused after 1 map' "$out/wizard-batch-1.log" || { echo "the batch did not pause after one map (see $out/wizard-batch-1.log)"; exit 1; }
timeout 600 "$repo/tools/capped.sh" "$godot" --headless --editor "${bargs[@]}" --wizard-resume >"$out/wizard-batch-2.log" 2>&1
grep -E '^ffx wizard: (DONE|FAIL)' "$out/wizard-batch-2.log" | tail -1 | cut -c1-400
grep -qE '^ffx wizard: DONE 2 map\(s\) done .* 1 skipped' "$out/wizard-batch-2.log" || { echo "the resumed batch did not finish as it should (see $out/wizard-batch-2.log)"; exit 1; }
# d. the classifier on the hand-labelled set
timeout 600 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-wizard-classifier --devsave=7 --documents="$out/docs" \
    --selftest-timeout=500 >"$out/wizard-classifier.log" 2>&1
cls=$?
rm -rf "$out/docs"
grep -E '^ffx selftest: classifier confusion \(all\)' "$out/wizard-classifier.log"
grep -E '^ffx selftest: (PASS|FAIL)' "$out/wizard-classifier.log" | tail -1 | cut -c1-1200 || echo "ffx selftest: no result (exit $cls, see $out/wizard-classifier.log)"
[[ $cls -eq 0 ]] || exit 1
[[ $own_check -eq 1 ]] && rm -rf "$repo/godot/remaster/check"
echo "remaster-wizard-check: PASS (logs, state and pictures in $out)"
