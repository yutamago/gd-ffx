#!/usr/bin/env bash
# R8, the remaster's HTML report (docs/remaster-editor.md, "The status dashboard"): the editor,
# headless, writes the status dashboard's rows as OUT_DIR/index.html with the last renders'
# pictures copied beside it (OUT_DIR/pictures/<map>/), then every link and picture of the page is
# checked (tools/compare/check-links.py). OUT_DIR is outside git (the pictures are the player's
# game); share the folder, not the repository.
#
#   FFX_GAME_DIR=<game> tools/remaster-report.sh OUT_DIR [-- EDITOR_ARGS...]
#
# EDITOR_ARGS: the folders the editor reads, as for the editor's own runs (e.g. --renders-dir=DIR,
# --remaster-budgets=DIR, --remaster-maps=DIR; the defaults: the project's files, user://remaster/renders,
# user://remaster/budgets).
set -u
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
out="${1:?OUT_DIR: where the report goes}"
shift
[[ "${1:-}" == -- ]] && shift
mkdir -p "$out"
out="$(cd "$out" && pwd)"
cd "$repo/godot"
timeout 600 "$repo/tools/capped.sh" "$godot" --headless --editor --path . -- --remaster-report="$out" "$@" >"$out/report.log" 2>&1
code=$?
grep -E '^ffx report: (DONE|FAIL)' "$out/report.log" | tail -1 || echo "ffx report: no result (exit $code, see $out/report.log)"
[[ $code -eq 0 && -f "$out/index.html" ]] || exit 1
python3 -I "$repo/tools/compare/check-links.py" "$out/index.html" || exit 1
echo "remaster-report: $out/index.html"
