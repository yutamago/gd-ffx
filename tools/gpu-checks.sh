#!/usr/bin/env bash
# R8, the standing GPU checks in one go (docs/running.md, "The GPU checks"): the Windows export
# (tools/capped.sh tools/export.sh windows first) on the machine's GPU, one check after another
# through tools/compare-shot-win.sh: silent (--audio-driver Dummy), its window behind every other
# window (--background-window), Vulkan (Forward+). Then a summary table.
#
#   FFX_GAME_DIR=<game> [FFX_REF_BESAID=REF.bmp] tools/gpu-checks.sh [OUT_DIR] [CHECK...]
#
# CHECK (default all, in this order):
#   classic      Besaid (save 7) classic: its picture, scored against FFX_REF_BESAID (the player's
#                screenshot of the original at that spot; none: the picture only)
#   lighting     --selftest-lighting: classic against remastered, R3's colour check
#   materialize  --selftest-materialize: the GPU material pipeline's parity with the CPU, its times
#   character    --selftest-character: the stand-in humanoid retargeted on Tidus
#   rebuild      --selftest-rebuild --devsave=14: ptkl14 rebuilt in 3D against its classic layers
#   effects      --selftest-effects: Besaid's effects by id, the example remake
#   budgets      tools/budget.sh windows besaid (only when named, or FFX_GPU_BUDGETS=1)
# OUT_DIR (default /tmp/gd-ffx-gpu-checks): a folder per check (pictures, logs: the player's game,
# never in git); the table also as OUT_DIR/summary.txt. Exit 1 when a check failed.
set -u
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-/tmp/gd-ffx-gpu-checks}"
[[ $# -gt 0 ]] && shift
checks=("$@")
if [[ ${#checks[@]} -eq 0 ]]; then
    checks=(classic lighting materialize character rebuild effects)
    [[ "${FFX_GPU_BUDGETS:-0}" == 1 ]] && checks+=(budgets)
fi
: "${FFX_GAME_DIR:?FFX_GAME_DIR: the game folder}"
[[ -f "$repo/build/windows/gd-ffx.console.exe" ]] || { echo "gpu-checks: no Windows export (tools/capped.sh tools/export.sh windows)" >&2; exit 2; }
mkdir -p "$out"
out="$(cd "$out" && pwd)"
rows=()
fail=0
run() { # NAME SAVE TEST REF
    local name=$1 save=$2 test=$3 ref=$4 dir="$out/$1" t0 t1 code line extra
    rm -rf "$dir"
    t0=$(date +%s)
    FFX_SHOT_TEST="$test" timeout 900 "$repo/tools/compare-shot-win.sh" "$ref" "$dir" "$save" vulkan >"$out/$name.txt" 2>&1
    code=$?
    t1=$(date +%s)
    line=$(grep -E 'ffx selftest: (PASS|FAIL)' "$dir/godot.log" 2>/dev/null | tail -1 | sed -E 's/^ffx selftest: //')
    case "$name" in
        classic) extra=$(grep -oE 'mean absolute difference [0-9.]+ / 255' "$out/$name.txt" | tail -1) ;;
        lighting) extra=$(grep -A4 'colour, remastered against classic' "$out/$name.txt" | grep -m1 -oE 'dL [^,]*, chroma [^,]*, hue [0-9.]+ deg') ;;
        materialize) extra=$(grep -m1 -oE 'at [0-9]+ px \(mean/p99/max of 255\): [^;]*; GPU bake [0-9]+ ms' "$dir/godot.log" 2>/dev/null | cut -c1-110) ;;
        rebuild) extra=$(grep -oE 'rebuilt against classic [0-9.]+/255' "$dir/godot.log" 2>/dev/null | tail -1) ;;
        *) extra="" ;;
    esac
    local result=FAIL
    [[ "$line" == PASS* && ( $code -eq 0 || "$name" == classic ) ]] && result=PASS
    [[ "$name" == classic && -f "$dir/godot.png" ]] && result=PASS
    [[ "$result" == PASS ]] || fail=1
    local renderer
    renderer=$(grep -oE 'Vulkan [0-9.]+[^-]*- Forward\+[^-]*- Using Device[^:]*: [^,]*' "$dir/godot.log" 2>/dev/null | head -1 | sed -E 's/.*Using Device[^:]*: //' | cut -c1-40)
    rows+=("$(printf '%-12s %-5s %5ss  %-40s %s' "$name" "$result" "$((t1 - t0))" "${extra:-${line:0:60}}" "${renderer:-}")")
    printf '%s\n' "${rows[-1]}"
}
for c in "${checks[@]}"; do
    case "$c" in
        classic) run classic 7 "--selftest-besaid --no-walk --selftest-timeout=12" "${FFX_REF_BESAID:--}" ;;
        lighting) run lighting 7 "--selftest-lighting --selftest-timeout=400" - ;;
        materialize) run materialize 7 "--selftest-materialize --selftest-timeout=600" - ;;
        character) run character 7 "--selftest-character --selftest-timeout=300" - ;;
        rebuild) run rebuild 14 "--selftest-rebuild --selftest-timeout=200" - ;;
        effects) run effects 7 "--selftest-effects --selftest-timeout=200" - ;;
        budgets)
            t0=$(date +%s)
            FFX_BUDGET_OUT="$out/budgets" "$repo/tools/budget.sh" windows besaid >"$out/budgets.txt" 2>&1
            code=$?
            t1=$(date +%s)
            r=PASS; [[ $code -eq 0 ]] || { r=FAIL; fail=1; }
            rows+=("$(printf '%-12s %-5s %5ss  %s' budgets "$r" "$((t1 - t0))" "$(grep -c '^bsil00' "$out/budgets.txt") runs, see $out/budgets.txt")")
            printf '%s\n' "${rows[-1]}"
            ;;
        *) echo "gpu-checks: unknown check $c" >&2; exit 2 ;;
    esac
done
{
    echo "GPU checks ($(date +%Y-%m-%d), the Windows export, Vulkan, --background-window):"
    printf '%-12s %-5s %6s  %s\n' check result time "key figure / note"
    printf '%s\n' "${rows[@]}"
    [[ $fail -eq 0 ]] && echo "gpu-checks: PASS" || echo "gpu-checks: FAIL (see $out/<check>.txt and $out/<check>/godot.log)"
} | tee "$out/summary.txt"
exit $fail
