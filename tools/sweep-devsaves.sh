#!/usr/bin/env bash
# G5's sweep: runs the map check (--selftest-map) headless on developer saves,
# one at a time through tools/capped.sh, and prints one line
# each: PASS/FAIL, the note, and the log's error count. With the Flash check
# (--check-flash: every Flash movie the game opens or shows has a native
# screen, else the save FAILs) each line also names the movies the game showed,
# and the end lists every movie opened and shown over the sweep. Logs go to
# OUT_DIR (default /tmp/gd-ffx-sweep); nothing is shown on screen.
#
#   FFX_GAME_DIR=<game> tools/sweep-devsaves.sh [quick|all|N,N,...|FIRST LAST] [OUT_DIR]
#
# quick (the default): ten saves that between them cover every kind of map
# and feature the full sweep has caught problems in (7 Besaid beach: 3D,
# objects, morphs, effects, distortion, save sphere, cloth; 8 Besaid village:
# 3D, exits to the huts; 12 S.S. Liki corridor: a squeezed 2D room; 14 Kilika:
# 2D layers; 22 Luca harbour: a busy map; 29 Luca stadium: NPC cloth; 52 Rin's
# agency: a 2D room with a reference picture; 61 Macalania: mist and pyrefly
# effects; 62 Bikanel: desert, the sun's flare; 71 Bevelle: a 2D scene switch).
# all: every developer save, 1..110, for changes that touch everything (the
# importer's format, the shaders, the loading path) and before a release.
# FFX_CLASSIC: the classic cache to use (default: the project's, user://classic)
# FFX_SWEEP_ARGS: more arguments for each run, e.g. "--render-path=remastered"
# (every map drawn by the remaster's lit path: docs/remaster-editor.md)
set -u
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
QUICK="7 8 12 14 22 29 52 61 62 71"
out=/tmp/gd-ffx-sweep
case "${1:-quick}" in
    quick) saves=$QUICK; out="${2:-$out}" ;;
    all) saves=$(seq 1 110); out="${2:-$out}" ;;
    *,*) saves=$(tr ',' ' ' <<<"$1"); out="${2:-$out}" ;;
    *) saves=$(seq "$1" "${2:-$1}"); out="${3:-$out}" ;;
esac
mkdir -p "$out"
rm -f "$out"/devsave-*.log
cd "$repo/godot"
for n in $saves; do
    log="$out/devsave-$n.log"
    timeout 240 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-map --check-flash --devsave="$n" \
        --selftest-timeout=150 --documents="$out/docs-$n" ${FFX_CLASSIC:+--classic="$FFX_CLASSIC"} ${FFX_SWEEP_ARGS:-} >"$log" 2>&1
    code=$?
    rm -rf "$out/docs-$n"
    result=$(grep -E '^ffx selftest: (PASS|FAIL)' "$log" | tail -1 | cut -c15-260)
    errors=$(grep -cE 'SCRIPT ERROR|^ERROR:|panicked' "$log")
    missing=$(grep 'not in the archive' "$log" | grep -vc '/titl/')
    shown=$(grep -E '^ffx flash: flash movies' "$log" | tail -1 | sed -E 's/.*; shown: ([^;]*);.*/\1/')
    echo "devsave $n: exit $code ${result:-no result} [errors $errors, missing classic $missing, flash shown: ${shown:-?}]"
done
# every movie over the sweep
opened=$(grep -hE '^ffx flash: flash movies' "$out"/devsave-*.log | sed -E 's/.*opened: ([^;]*);.*/\1/' | tr ' ' '\n' | sort -u | tr '\n' ' ')
shown=$(grep -hE '^ffx flash: flash movies' "$out"/devsave-*.log | sed -E 's/.*; shown: ([^;]*);.*/\1/' | tr ' ' '\n' | grep -v '^none$' | sort -u | tr '\n' ' ')
without=$(grep -hE 'WITHOUT a native screen|NOT DRAWN' "$out"/devsave-*.log | head -5)
echo "flash movies opened over the sweep: $opened"
echo "flash movies shown over the sweep: $shown"
echo "flash movies without a native screen: ${without:-none}"
