#!/usr/bin/env bash
# G5's sweep: runs the map check (--selftest-map) headless on developer saves,
# one at a time through tools/capped.sh, and prints one line
# each: PASS/FAIL, the note, and the log's error count. With the Flash check
# (--check-flash: every Flash movie the game opens or shows has a native
# screen, else the save FAILs) each line also names the movies the game showed,
# and the end lists every movie opened and shown over the sweep. Logs go to
# OUT_DIR (default /tmp/gd-ffx-sweep); nothing is shown on screen.
#
#   FFX_GAME_DIR=<game> tools/sweep-devsaves.sh [--pictures] [quick|all|N,N,...|FIRST LAST] [OUT_DIR]
#   tools/sweep-devsaves.sh accept [OUT_DIR] [N,N,...]   # the last picture run's pictures become the baseline
#   tools/sweep-devsaves.sh calibrate [OUT_DIR] [N,N,...]  # the last run's differences (nothing changed) as each save's noise
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
#
# R8, regression pictures (--pictures, or FFX_SWEEP_PICTURES=1): each run under
# Xvfb (OpenGL, Compatibility; nothing on screen) saves its picture,
# OUT_DIR/devsave-N.png, taken with the views held at one game update
# (--hold-shot: 150 updates after the map has been shown 3 s; with a baseline,
# the baseline's update, --hold-update=U; the last 30 updates before it at a
# quarter of the game's speed, so the views hold at U itself), so the sea, the
# effects and the party show the same moment; then OUT_DIR/devsave-N-map.png,
# the same moment without the game's characters (the NPCs' wandering differs
# from run to run even at one update). The map picture is compared with the
# baseline's (tools/compare/regress.py: the mean absolute difference per
# channel, 0..255, the share of pixels off by more than 24, the worst cell;
# OUT_DIR/devsave-N-map-diff.png), the whole picture reported beside it
# (devsave-N-diff.png). Some maps' animation differs between runs at the same
# update (Besaid's foliage, Luca's banner, Macalania's pyreflies and mist), so
# each save has its noise: `calibrate` after a run against the baseline with
# nothing changed keeps the run's difference per save (the most seen), and a
# save's limit is 1.5 x its noise + 0.3, at least FFX_SWEEP_THRESHOLD (default
# 0.5); a save with noise over 3 is marked loose (Luca's harbour at save 22 is
# in a cut scene whose camera is elsewhere at the same update: it catches only
# large breaks). Over its limit the save's line says REGRESSED and the sweep exits 1. The
# baseline lives outside git (pictures of the game): FFX_SWEEP_BASELINE
# (default ~/.cache/gd-ffx/sweep-baseline/), a picture, its map picture, its
# update and its noise per save; `accept` copies the last picture run's into it
# (its noise cleared: calibrate again).
set -u
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
QUICK="7 8 12 14 22 29 52 61 62 71"
out=/tmp/gd-ffx-sweep
baseline="${FFX_SWEEP_BASELINE:-$HOME/.cache/gd-ffx/sweep-baseline}"
threshold="${FFX_SWEEP_THRESHOLD:-0.5}"
pictures="${FFX_SWEEP_PICTURES:-0}"
if [[ "${1:-}" == accept ]]; then
    from="${2:-$out}"
    mkdir -p "$baseline"
    n=0
    for f in "$from"/devsave-*.png; do
        [[ -f "$f" && "$f" != *-diff.png && "$f" != *-map.png ]] || continue
        s=$(basename "$f" .png); s=${s#devsave-}
        if [[ -n "${3:-}" && ",$3," != *",$s,"* ]]; then continue; fi
        u=$(grep -oE 'hold: held at update [0-9]+' "$from/devsave-$s.log" 2>/dev/null | tail -1 | grep -oE '[0-9]+$')
        [[ -n "$u" ]] || { echo "accept: devsave $s: no held update in its log, not taken"; continue; }
        rm -f "$baseline/devsave-$s.noise"
        cp "$f" "$baseline/devsave-$s.png"
        [[ -f "$from/devsave-$s-map.png" ]] && cp "$from/devsave-$s-map.png" "$baseline/devsave-$s-map.png"
        echo "$u" >"$baseline/devsave-$s.update"
        n=$((n + 1))
    done
    echo "accept: $n picture(s) into $baseline (calibrate next: a run against it, then tools/sweep-devsaves.sh calibrate)"
    exit 0
fi
if [[ "${1:-}" == calibrate ]]; then
    # the last picture run's differences (nothing meant to change) as each save's noise, the most seen kept
    from="${2:-$out}"
    n=0
    for f in "$from"/devsave-*.mean; do
        [[ -f "$f" ]] || continue
        s=$(basename "$f" .mean); s=${s#devsave-}
        if [[ -n "${3:-}" && ",$3," != *",$s,"* ]]; then continue; fi
        [[ -f "$baseline/devsave-$s.png" ]] || continue
        old=$(cat "$baseline/devsave-$s.noise" 2>/dev/null || echo 0)
        new=$(cat "$f")
        awk -v a="$old" -v b="$new" 'BEGIN { printf "%.2f\n", (a > b ? a : b) }' >"$baseline/devsave-$s.noise"
        echo "calibrate: devsave $s: noise $(cat "$baseline/devsave-$s.noise")/255 (this run $new, before $old)"
        n=$((n + 1))
    done
    echo "calibrate: $n save(s) in $baseline"
    exit 0
fi
if [[ "${1:-}" == --pictures ]]; then
    pictures=1
    shift
fi
case "${1:-quick}" in
    quick) saves=$QUICK; out="${2:-$out}" ;;
    all) saves=$(seq 1 110); out="${2:-$out}" ;;
    *,*) saves=$(tr ',' ' ' <<<"$1"); out="${2:-$out}" ;;
    *) if [[ "${2:-}" =~ ^[0-9]+$ ]]; then saves=$(seq "$1" "$2"); out="${3:-$out}"; else saves=$1; out="${2:-$out}"; fi ;;
esac
mkdir -p "$out"
out="$(cd "$out" && pwd)"
rm -f "$out"/devsave-*.log "$out"/devsave-*.png "$out"/devsave-*.mean
cd "$repo/godot"
regressed=0
for n in $saves; do
    log="$out/devsave-$n.log"
    args=(--selftest-map --check-flash --devsave="$n" --selftest-timeout=150 --documents="$out/docs-$n"
        ${FFX_CLASSIC:+--classic="$FFX_CLASSIC"})
    if [[ "$pictures" == 1 ]]; then
        hold=(--hold-shot)
        [[ -f "$baseline/devsave-$n.update" ]] && hold=(--hold-update="$(cat "$baseline/devsave-$n.update")")
        # shellcheck disable=SC2086
        env -u WAYLAND_DISPLAY -u DISPLAY timeout 300 "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1280x720x24" \
            "$godot" --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy --path . -- "${args[@]}" \
            --no-overlay --shot="$out/devsave-$n.png" "${hold[@]}" ${FFX_SWEEP_ARGS:-} >"$log" 2>&1
    else
        # shellcheck disable=SC2086
        timeout 240 "$repo/tools/capped.sh" "$godot" --headless --path . -- "${args[@]}" ${FFX_SWEEP_ARGS:-} >"$log" 2>&1
    fi
    code=$?
    rm -rf "$out/docs-$n"
    result=$(grep -E '^ffx selftest: (PASS|FAIL)' "$log" | tail -1 | cut -c15-260)
    errors=$(grep -cE 'SCRIPT ERROR|^ERROR:|panicked' "$log")
    missing=$(grep 'not in the archive' "$log" | grep -vc '/titl/')
    shown=$(grep -E '^ffx flash: flash movies' "$log" | tail -1 | sed -E 's/.*; shown: ([^;]*);.*/\1/')
    picture=""
    if [[ "$pictures" == 1 ]]; then
        held=$(grep -oE 'hold: held at update [0-9]+[^)]*' "$log" | tail -1 | cut -c7-)
        if [[ ! -f "$out/devsave-$n.png" ]]; then
            picture="; picture: none"
            regressed=1
        elif [[ -f "$baseline/devsave-$n-map.png" && -f "$out/devsave-$n-map.png" ]]; then
            # the map without its characters against the threshold; the whole picture reported beside it
            # the save's threshold: the noise its calibration saw (1.5 x + 0.3), at least FFX_SWEEP_THRESHOLD
            noise=$(cat "$baseline/devsave-$n.noise" 2>/dev/null || echo 0)
            limit=$(awk -v t="$threshold" -v z="$noise" 'BEGIN { l = 1.5 * z + 0.3; printf "%.2f", (l > t ? l : t) }')
            line=$(uv run -q --with pillow python -I "$repo/tools/compare/regress.py" "$baseline/devsave-$n-map.png" "$out/devsave-$n-map.png" \
                "$out/devsave-$n-map-diff.png" "$limit" 2>&1)
            rc=$?
            grep -oE '^mean [0-9.]+' <<<"$line" | cut -c6- >"$out/devsave-$n.mean"
            if [[ ! -f "$baseline/devsave-$n.noise" ]]; then line="$line (not calibrated)"
            elif awk -v z="$noise" 'BEGIN { exit !(z > 3) }'; then line="$line (loose: noise $noise, the map moves between runs)"; fi
            whole=$(uv run -q --with pillow python -I "$repo/tools/compare/regress.py" "$baseline/devsave-$n.png" "$out/devsave-$n.png" \
                "$out/devsave-$n-diff.png" 255 2>&1 | sed -E 's/ \(threshold [^)]*\)//; s/: ok$//')
            picture="; picture ${held:-not held}: the map $line; with the characters $whole"
            [[ $rc -eq 0 ]] || regressed=1
        else
            picture="; picture ${held:-not held}: no baseline (tools/sweep-devsaves.sh accept $out)"
        fi
    fi
    echo "devsave $n: exit $code ${result:-no result} [errors $errors, missing classic $missing, flash shown: ${shown:-?}]$picture"
done
# every movie over the sweep
opened=$(grep -hE '^ffx flash: flash movies' "$out"/devsave-*.log | sed -E 's/.*opened: ([^;]*);.*/\1/' | tr ' ' '\n' | sort -u | tr '\n' ' ')
shown=$(grep -hE '^ffx flash: flash movies' "$out"/devsave-*.log | sed -E 's/.*; shown: ([^;]*);.*/\1/' | tr ' ' '\n' | grep -v '^none$' | sort -u | tr '\n' ' ')
without=$(grep -hE 'WITHOUT a native screen|NOT DRAWN' "$out"/devsave-*.log | head -5)
echo "flash movies opened over the sweep: $opened"
echo "flash movies shown over the sweep: $shown"
echo "flash movies without a native screen: ${without:-none}"
if [[ "$pictures" == 1 ]]; then
    echo "pictures: $out/devsave-N.png against $baseline (threshold $threshold/255)$([[ $regressed -eq 1 ]] && echo ': REGRESSED')"
    exit $regressed
fi
