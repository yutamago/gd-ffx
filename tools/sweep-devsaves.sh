#!/usr/bin/env bash
# G5's sweep: runs the map check (--selftest-map) headless on developer saves
# (default 1..110), one at a time through tools/capped.sh, and prints one line
# each: PASS/FAIL, the note, and the log's error count. With the Flash check
# (--check-flash: every Flash movie the game opens or shows has a native
# screen, else the save FAILs) each line also names the movies the game showed,
# and the end lists every movie opened and shown over the sweep. Logs go to
# OUT_DIR (default /tmp/gd-ffx-sweep); nothing is shown on screen.
#
#   FFX_GAME_DIR=<game> tools/sweep-devsaves.sh [FIRST LAST] [OUT_DIR]
# FFX_CLASSIC: the classic cache to use (default: the project's, user://classic)
set -u
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
first="${1:-1}"; last="${2:-110}"; out="${3:-/tmp/gd-ffx-sweep}"
mkdir -p "$out"
cd "$repo/godot"
for n in $(seq "$first" "$last"); do
    log="$out/devsave-$n.log"
    timeout 240 "$repo/tools/capped.sh" "$godot" --headless --path . -- --selftest-map --check-flash --devsave="$n" \
        --selftest-timeout=150 --documents="$out/docs-$n" ${FFX_CLASSIC:+--classic="$FFX_CLASSIC"} >"$log" 2>&1
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
