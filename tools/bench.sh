#!/usr/bin/env bash
# G5's benchmark (docs/running.md, "Benchmark"): Godot headless (the CPU side:
# the dummy renderer draws nothing) at developer saves, one run at a time
# through tools/capped.sh, each recording --bench-seconds of frames once the
# scene is shown and printing the `ffx bench:` lines (frame times, each view's
# CPU time, allocations and GC, the game's updates) and the game thread's
# hitches (ffx-host's `hitch:` lines, here over FFX_HOST_HITCH_MS, 50 ms by
# default).
#
#   FFX_GAME_DIR=<game folder> tools/bench.sh [SCENE...] [-- GODOT_ARGS...]
#
# SCENE: besaid (save 7), besaid-walk (save 7, walking off the beach),
# lchb (save 22, 13 models), battle (save 7, battle 3,0,0), battle-cloth (save 12, battle
# 34,0,0: three characters with cloth); default: besaid
# lchb battle. Logs in $BENCH_OUT (default /tmp/gd-ffx-bench), removed with
# the documents unless BENCH_KEEP=1.
#
# BENCH_PROGRAM=<exported program> (tools/export.sh: build/linux/gd-ffx.x86_64)
# runs that instead of the editor on the project: the C# assembly built
# ExportRelease and .NET started with the project's runtimeconfig.json (Godot
# run on the project folder uses the Debug build and the editor's own).
#
# BENCH_RENDER=1 renders offscreen instead (Xvfb, OpenGL; nothing on screen,
# vsync off): Godot's draw calls and objects per frame. Without a GPU (WSL:
# llvmpipe on the CPU) its frame times say nothing about a real machine.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
: "${FFX_GAME_DIR:?set FFX_GAME_DIR to the game folder (the one with FFX.exe)}"
scenes=()
while [[ $# -gt 0 && "$1" != -- ]]; do
    scenes+=("$1")
    shift
done
[[ "${1:-}" == -- ]] && shift
[[ ${#scenes[@]} -gt 0 ]] || scenes=(besaid lchb battle)
out="${BENCH_OUT:-/tmp/gd-ffx-bench}"
mkdir -p "$out"
program="${BENCH_PROGRAM:-}"
if [[ -n "$program" ]]; then
    [[ -x "$program" ]] || { echo "bench: no program $program" >&2; exit 2; }
    program="$(cd "$(dirname "$program")" && pwd)/$(basename "$program")"
fi
cd "$repo/godot"
if [[ -z "$program" ]]; then
    dotnet build -nologo -v quiet gd-ffx.csproj ${BENCH_DOTNET_ARGS:-} >/dev/null
    [[ -f .godot/extension_list.cfg ]] || "$godot" --headless --editor --quit --path . >/dev/null 2>&1 || true
fi
docs="$(mktemp -d -t gd-ffx-bench.XXXXXX)"
trap 'rm -rf "$docs"; [[ "${BENCH_KEEP:-0}" == 1 ]] || rm -rf "$out"' EXIT
status=0
for scene in "${scenes[@]}"; do
    case "$scene" in
        besaid) args=(--devsave=7) ;;
        besaid-walk) args=(--devsave=7 --bench-walk) ;;
        lchb) args=(--devsave=22) ;;
        battle) args=(--devsave=7 --battle=3,0,0) ;;
        # Tidus, Yuna and Lulu: 38 cloth segments, about 1,400 simulated vertices
        battle-cloth) args=(--devsave=12 --battle=34,0,0) ;;
        *) echo "bench: unknown scene $scene" >&2; exit 2 ;;
    esac
    echo "== $scene"
    set +e
    if [[ -n "$program" ]]; then
        target=("$program")
    else
        target=("$godot" --path .)
    fi
    if [[ "${BENCH_RENDER:-0}" == 1 ]]; then
        run=(env -u WAYLAND_DISPLAY -u DISPLAY "$repo/tools/capped.sh" xvfb-run -a -s "-screen 0 1280x720x24"
            "${target[@]}" --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy)
        args+=(--no-vsync)
    else
        run=("$repo/tools/capped.sh" "${target[@]}" --headless)
    fi
    FFX_HOST_HITCH_MS="${FFX_HOST_HITCH_MS:-50}" timeout 400 "${run[@]}" -- \
        --bench "${args[@]}" --selftest-timeout=300 --documents="$docs" "$@" >"$out/$scene.log" 2>&1
    s=$?
    set -e
    [[ $s -eq 0 ]] || status=$s
    grep -E '^ffx bench|^ffx selftest|hitch|SCRIPT ERROR|panicked' "$out/$scene.log" | cut -c1-1200
done
exit "$status"
