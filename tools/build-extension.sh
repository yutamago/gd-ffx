#!/usr/bin/env bash
# Builds the GDExtension (rust/ffx-gd: ffx-rust inside Godot) for the Godot
# project in godot/ (docs/building.md).
#
#   tools/build-extension.sh [linux|windows] [build|clippy|test] [cargo args...]
#
# linux    rust/target/release/libffx_gd.so (the default on Linux)
# windows  rust/target/x86_64-pc-windows-msvc/release/ffx_gd.dll, cross-built
#          from Linux/WSL with cargo-xwin, as ffx-rust builds ffx-rust.exe
#          (its tools/build-host.sh): Unicorn's cmake build needs cmake, ninja,
#          llvm-lib, llvm-ml64 and a toolchain file without
#          CMAKE_MSVC_RUNTIME_LIBRARY; FFX_RUST_DIR must name an ffx-rust
#          checkout for its tools/shims and tools/xwin-env.sh.
#
# FFX_RUST_DIR  an ffx-rust checkout to build against instead of the git
#               dependency (a branch not merged yet, local changes): passed to
#               cargo as [patch] entries.
# FFX_GD_FEATURES  cargo features (default: the crate's, i.e. dev-unicorn);
#               "none" for none (a build without Unicorn: see docs/building.md).
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
platform=linux
[[ "$(uname -s)" == MINGW* || "$(uname -s)" == MSYS* ]] && platform=windows
if [[ "${1:-}" == linux || "${1:-}" == windows ]]; then
    platform=$1
    shift
fi
cmd=build
if [[ "${1:-}" == build || "${1:-}" == clippy || "${1:-}" == test ]]; then
    cmd=$1
    shift
fi
args=(--release)
if [[ -n "${FFX_RUST_DIR:-}" ]]; then
    ffx="$(cd "$FFX_RUST_DIR" && pwd)"
    for c in ffx-host ffx-bridge ffx-state; do
        args+=(--config "patch.\"https://github.com/yutamago/ffx-rust\".$c.path=\"$ffx/crates/$c\"")
    done
fi
case "${FFX_GD_FEATURES-}" in
    "") ;;
    none) args+=(--no-default-features) ;;
    *) args+=(--no-default-features --features "$FFX_GD_FEATURES") ;;
esac
cd "$repo/rust"
if [[ "$platform" == linux ]]; then
    cargo "$cmd" "${args[@]}" "$@"
    [[ "$cmd" == build ]] && echo "built: ${CARGO_TARGET_DIR:-$repo/rust/target}/release/libffx_gd.so"
    exit 0
fi

# ---- windows: cargo-xwin, as ffx-rust's tools/build-host.sh -------------------
target=x86_64-pc-windows-msvc
: "${FFX_RUST_DIR:?the Windows build needs FFX_RUST_DIR (an ffx-rust checkout: its tools/shims and tools/xwin-env.sh)}"
shims="$repo/rust/target/host-shims"
mkdir -p "$shims"
if ! cmake --version >/dev/null 2>&1; then
    printf '#!/bin/bash\nexec uvx --from cmake cmake "$@"\n' >"$shims/cmake"
    chmod +x "$shims/cmake"
fi
if ! command -v ninja >/dev/null; then
    printf '#!/bin/bash\nexec uvx --from ninja ninja "$@"\n' >"$shims/ninja"
    chmod +x "$shims/ninja"
fi
if ! command -v llvm-lib >/dev/null; then
    lib="$(compgen -c llvm-lib- | grep -E '^llvm-lib-[0-9]+$' | sort -t- -k3 -n | tail -1 || true)"
    [[ -n "$lib" ]] && ln -sf "$(command -v "$lib")" "$shims/llvm-lib"
fi
if ! command -v ml64 >/dev/null; then
    ml="$(compgen -c llvm-ml64- | grep -E '^llvm-ml64-[0-9]+$' | sort -t- -k3 -n | tail -1 || true)"
    [[ -n "$ml" ]] && ln -sf "$(command -v "$ml")" "$shims/ml64"
fi
export PATH="$ffx/tools/shims:$shims:$PATH"
export XWIN_ARCH=x86,x86_64
toolchain_env=()
xwin_toolchain="${XDG_CACHE_HOME:-$HOME/.cache}/cargo-xwin/cmake/clang-cl/$target-toolchain.cmake"
eval "$("$ffx/tools/xwin-env.sh" "$target")"
if [[ -f "$xwin_toolchain" ]]; then
    toolchain="$shims/$target-toolchain.cmake"
    grep -v CMAKE_MSVC_RUNTIME_LIBRARY "$xwin_toolchain" |
        sed "s|\${CMAKE_CURRENT_LIST_DIR}|$(dirname "$xwin_toolchain")|" >"$toolchain"
    toolchain_env=("CMAKE_TOOLCHAIN_FILE_$target=$toolchain")
fi
env "${toolchain_env[@]}" cargo "$cmd" --target "$target" "${args[@]}" "$@"
[[ "$cmd" == build ]] && echo "built: ${CARGO_TARGET_DIR:-$repo/rust/target}/$target/release/ffx_gd.dll"
exit 0
