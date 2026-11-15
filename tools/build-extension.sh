#!/usr/bin/env bash
# Builds the GDExtension (rust/ffx-gd: ffx-rust inside Godot) for the Godot
# project in godot/ (docs/building.md).
#
#   tools/build-extension.sh [linux|windows] [build|clippy|test] [cargo args...]
#   tools/build-extension.sh args     the cargo arguments it would pass (the
#                                     ffx-rust [patch] entries, the features),
#                                     one per line (tools/export.sh's notices)
#
# linux    rust/target/release/libffx_gd.so (the default on Linux)
# windows  rust/target/x86_64-pc-windows-msvc/release/ffx_gd.dll, cross-built
#          from Linux/WSL with cargo-xwin, as ffx-rust builds ffx-rust.exe
#          (its tools/build-host.sh): Unicorn's cmake build needs cmake, ninja,
#          llvm-lib, llvm-ml64 and a toolchain file without
#          CMAKE_MSVC_RUNTIME_LIBRARY; ffx-rust's tools/shims and
#          tools/xwin-env.sh come from FFX_RUST_DIR, else from cargo's checkout
#          of the ffx-rust revision in rust/Cargo.lock.
#
# FFX_RUST_DIR  an ffx-rust checkout to build against instead of the git
#               dependency (a branch not merged yet, local changes): passed to
#               cargo as [patch] entries.
# FFX_GD_FEATURES  cargo features (default: the crate's, i.e. dev-unicorn);
#               "none" for none (a build without Unicorn: see docs/building.md).
# FFX_GD_RUSTFLAGS  added to RUSTFLAGS (tools/export.sh: --remap-path-prefix,
#               so no path of the build machine ends up in an export).
# FFX_GD_CFLAGS  added to the target's C and C++ flags (the same for C code's
#               __FILE__: Unicorn's, in a dev-unicorn build).
# CARGO_TARGET_DIR  as with cargo (tools/export.sh builds into rust/target/export).
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
# cargo's --config, RUSTFLAGS and the C flags split on spaces
for p in "$repo" "$HOME" "${FFX_RUST_DIR:-}" "${CARGO_TARGET_DIR:-}"; do
    if [[ "$p" == *" "* ]]; then
        echo "build-extension.sh: a path with spaces is not supported: '$p'" >&2
        exit 2
    fi
done
platform=linux
[[ "$(uname -s)" == MINGW* || "$(uname -s)" == MSYS* ]] && platform=windows
if [[ "${1:-}" == linux || "${1:-}" == windows ]]; then
    platform=$1
    shift
fi
cmd=build
if [[ "${1:-}" == build || "${1:-}" == clippy || "${1:-}" == test || "${1:-}" == args ]]; then
    cmd=$1
    shift
fi
args=(--release)
if [[ -n "${FFX_RUST_DIR:-}" ]]; then
    ffx="$(cd "$FFX_RUST_DIR" && pwd)"
    for c in ffx-host ffx-bridge ffx-state ffx-phyre ffx-vbf ffx-text; do
        args+=(--config "patch.\"https://github.com/yutamago/ffx-rust\".$c.path=\"$ffx/crates/$c\"")
    done
fi
case "${FFX_GD_FEATURES-}" in
    "") ;;
    none) args+=(--no-default-features) ;;
    *) args+=(--no-default-features --features "$FFX_GD_FEATURES") ;;
esac
if [[ "$cmd" == args ]]; then
    [[ ${#args[@]} -gt 1 ]] && printf '%s\n' "${args[@]:1}"
    exit 0
fi
cd "$repo/rust"
shims="$repo/rust/target/host-shims"
mkdir -p "$shims"
if ! cmake --version >/dev/null 2>&1; then
    printf '#!/bin/bash\nexec uvx --from cmake cmake "$@"\n' >"$shims/cmake"
    chmod +x "$shims/cmake"
fi
if [[ "$platform" == linux ]]; then
    # cmake for Unicorn's own build (dev-unicorn, when no prebuilt library is
    # found through pkg-config: UNICORN_NO_PKG_CONFIG=1, as tools/export.sh sets)
    export PATH="$shims:$PATH"
    if [[ -n "${FFX_GD_RUSTFLAGS:-}" ]]; then
        export RUSTFLAGS="${RUSTFLAGS:-} $FFX_GD_RUSTFLAGS"
    fi
    if [[ -n "${FFX_GD_CFLAGS:-}" ]]; then
        export CFLAGS_x86_64_unknown_linux_gnu="${CFLAGS_x86_64_unknown_linux_gnu:-} $FFX_GD_CFLAGS"
        export CXXFLAGS_x86_64_unknown_linux_gnu="${CXXFLAGS_x86_64_unknown_linux_gnu:-} $FFX_GD_CFLAGS"
    fi
    cargo "$cmd" "${args[@]}" "$@"
    [[ "$cmd" == build ]] && echo "built: ${CARGO_TARGET_DIR:-$repo/rust/target}/release/libffx_gd.so"
    exit 0
fi

# ---- windows: cargo-xwin, as ffx-rust's tools/build-host.sh -------------------
target=x86_64-pc-windows-msvc
# ffx-rust's tools/shims and tools/xwin-env.sh: FFX_RUST_DIR's, else those of
# cargo's checkout of the locked ffx-rust revision (where ffx-host comes from)
if [[ -z "${ffx:-}" ]]; then
    ffx="$(cargo metadata -q --format-version 1 | python3 -I -c '
import json, pathlib, sys
m = json.load(sys.stdin)
p = [p for p in m["packages"] if p["name"] == "ffx-host"]
print(pathlib.Path(p[0]["manifest_path"]).parents[2] if p else "")')"
fi
[[ -x "${ffx:-}/tools/xwin-env.sh" ]] || {
    echo "build-extension.sh: no ffx-rust tools/xwin-env.sh (${ffx:-ffx-rust not found}); set FFX_RUST_DIR" >&2
    exit 2
}
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
# cargo-xwin sets RUSTFLAGS (empty), which wins over the target's rustflags
if [[ -n "${FFX_GD_RUSTFLAGS:-}" ]]; then
    export RUSTFLAGS="${RUSTFLAGS:-} $FFX_GD_RUSTFLAGS"
fi
if [[ -n "${FFX_GD_CFLAGS:-}" ]]; then
    export CFLAGS_x86_64_pc_windows_msvc="${CFLAGS_x86_64_pc_windows_msvc:-} $FFX_GD_CFLAGS"
    export CXXFLAGS_x86_64_pc_windows_msvc="${CXXFLAGS_x86_64_pc_windows_msvc:-} $FFX_GD_CFLAGS"
fi
if [[ -f "$xwin_toolchain" ]]; then
    toolchain="$shims/$target-toolchain.cmake"
    grep -v CMAKE_MSVC_RUNTIME_LIBRARY "$xwin_toolchain" |
        sed "s|\${CMAKE_CURRENT_LIST_DIR}|$(dirname "$xwin_toolchain")|" >"$toolchain"
    toolchain_env=("CMAKE_TOOLCHAIN_FILE_$target=$toolchain")
fi
env "${toolchain_env[@]}" cargo "$cmd" --target "$target" "${args[@]}" "$@"
[[ "$cmd" == build ]] && echo "built: ${CARGO_TARGET_DIR:-$repo/rust/target}/$target/release/ffx_gd.dll"
exit 0
