#!/usr/bin/env bash
# R8, the performance budgets (docs/running.md, "Budgets"): the benchmark
# (--bench) on developer saves, classic and remastered, at the Steam Deck preset
# (1280x800, --quality=deck: 3D at 75 %, the low lighting quality, no GI) and at
# desktop settings (1920x1080, --quality=high; remastered with VoxelGI and
# without, for the GI's cost), uncapped (vsync and the frame cap off) so the
# frame time shows the headroom. Each run's figures (frame and GPU ms, draw
# calls, texture and video memory, the GI drawn) are its --bench-json record;
# tools/compare/budget.py holds them against each preset's budgets and prints
# the table. Nothing on screen: the Windows export behind every window
# (--background-window, Dummy audio), or Xvfb on Linux.
#
#   FFX_GAME_DIR=<game> tools/budget.sh [windows|linux] [SCENE...]
#
# windows (the default on WSL): build/windows/gd-ffx.console.exe (tools/export.sh
#   windows first), Vulkan, Forward+: the machine's GPU.
# linux: Godot on the project under Xvfb, OpenGL (Compatibility) on llvmpipe, or
#   FFX_BUDGET_DRIVER=vulkan (Forward+ on lavapipe): the CPU renders, so its
#   numbers are marked as such and have no budget.
# SCENE: besaid (save 7, the default), village (8), kilika (14), luca (22),
#   macalania (61), or a save number.
# FFX_BUDGET_RUNS: which runs (default "classic-deck remastered-deck classic-desktop
#   remastered-desktop remastered-desktop-gi").
# FFX_BUDGET_SECONDS: the window (default 10 s after a 4 s settle).
# Logs and records in FFX_BUDGET_OUT (default /tmp/gd-ffx-budget); the table as
# OUT/budgets.json, copied for the editor's dashboard to FFX_BUDGET_DIR (default
# the project's user folder: remaster/budgets/<platform>.json there).
set -u
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
platform=windows
grep -qi microsoft /proc/version 2>/dev/null || platform=linux
if [[ "${1:-}" == windows || "${1:-}" == linux ]]; then platform=$1; shift; fi
scenes=("$@")
[[ ${#scenes[@]} -gt 0 ]] || scenes=(besaid)
runs="${FFX_BUDGET_RUNS:-classic-deck remastered-deck classic-desktop remastered-desktop remastered-desktop-gi}"
seconds="${FFX_BUDGET_SECONDS:-10}"
out="${FFX_BUDGET_OUT:-/tmp/gd-ffx-budget}"
dash="${FFX_BUDGET_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/godot/app_userdata/gd-ffx/remaster/budgets}"
game="${FFX_GAME_DIR:?FFX_GAME_DIR: the game folder}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
rm -f "$out"/*.json "$out"/*.log
exe="$repo/build/windows/gd-ffx.console.exe"
if [[ "$platform" == windows ]]; then
    [[ -f "$exe" ]] || { echo "budget: no $exe (tools/capped.sh tools/export.sh windows)" >&2; exit 2; }
    chmod +x "$exe" "$repo/build/windows/gd-ffx.exe"
    win() { wslpath -w "$1"; }
else
    (cd "$repo/godot" && dotnet build -nologo -v quiet gd-ffx.csproj >/dev/null)
fi
save_of() {
    case "$1" in
        besaid) echo 7 ;; village) echo 8 ;; kilika) echo 14 ;; luca) echo 22 ;; macalania) echo 61 ;;
        *[!0-9]*) echo "budget: unknown scene $1" >&2; return 1 ;;
        *) echo "$1" ;;
    esac
}
for scene in "${scenes[@]}"; do
    save=$(save_of "$scene") || exit 2
    for run in $runs; do
        path=${run%%-*}
        rest=${run#*-}
        preset=${rest%%-*}
        args=(--bench --devsave="$save" --bench-settle=4 --bench-seconds="$seconds" --bench-uncapped --no-vsync --no-overlay
            --render-path="$path" --budget-preset="$preset" --selftest-timeout=240)
        if [[ "$preset" == deck ]]; then args+=(--quality=deck); size=1280x800
        else args+=(--quality=high); size=1920x1080; fi
        if [[ "$path" == remastered && "$preset" == desktop ]]; then
            [[ "$run" == *-gi ]] && args+=(--gi=voxelgi) || args+=(--gi=none)
        fi
        name="$scene-$run"
        echo "== $name ($platform, save $save, $size)"
        mkdir -p "$out/docs-$name"
        if [[ "$platform" == windows ]]; then
            timeout 400 "$exe" --audio-driver Dummy --rendering-driver vulkan --resolution "$size" -- \
                --game-dir="$(win "$(readlink -f "$game")")" --documents="$(win "$out/docs-$name")" --background-window \
                "${args[@]}" --bench-json="$(win "$out/$name.json")" 2>&1 | tr -d '\r' >"$out/$name.log"
        else
            w=${size%x*}; h=${size#*x}
            (cd "$repo/godot" && env -u WAYLAND_DISPLAY -u DISPLAY timeout 400 "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 ${w}x${h}x24" \
                "$godot" --display-driver x11 --rendering-driver "${FFX_BUDGET_DRIVER:-opengl3}" --audio-driver Dummy --resolution "$size" --path . -- \
                "${args[@]}" --documents="$out/docs-$name" --bench-json="$out/$name.json") >"$out/$name.log" 2>&1
        fi
        rm -rf "$out/docs-$name"
        grep -E '^ffx bench: (frames|render)|^ffx selftest: (PASS|FAIL)' "$out/$name.log" | cut -c1-220
        [[ -f "$out/$name.json" ]] || echo "budget: $name: no record (see $out/$name.log)"
    done
done
shopt -s nullglob
records=("$out"/*-*.json)
[[ ${#records[@]} -gt 0 ]] || { echo "budget: no records"; exit 1; }
python3 -I "$repo/tools/compare/budget.py" "$out/budgets.json" "${records[@]}"
status=$?
mkdir -p "$dash"
cp "$out/budgets.json" "$dash/$platform${FFX_BUDGET_DRIVER:+-$FFX_BUDGET_DRIVER}.json"
echo "budget: table in $out/budgets.json; for the dashboard: $dash/$platform${FFX_BUDGET_DRIVER:+-$FFX_BUDGET_DRIVER}.json"
exit $status
