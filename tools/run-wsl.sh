#!/usr/bin/env bash
# Runs gd-ffx in a window under WSLg (WSL2's own display): the OpenGL
# renderer, since Godot's Vulkan path draws a white window there (llvmpipe).
# Inside the shared memory cap (tools/capped.sh).
#
#   FFX_GAME_DIR=<game folder> tools/run-wsl.sh [-- --devsave=7 ...]
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
[[ "${1:-}" == -- ]] && shift
cd "$repo/godot"
exec "$repo/tools/capped.sh" "$godot" --rendering-driver opengl3 --path . -- "$@"
