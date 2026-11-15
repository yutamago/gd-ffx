#!/usr/bin/env bash
# Exports gd-ffx as a standalone program (docs/exporting.md).
#
#   tools/export.sh [linux|windows|all]          (default: linux)
#
# For each platform: builds the extension (release, into rust/target/export,
# every build path remapped), the C# assembly, and runs Godot's export
# (godot/export_presets.cfg, presets "Linux" and "Windows": .NET, the PCK
# embedded in the program); then adds README.txt and licences/ and checks the
# result (tools/export-files.py check: its files, no game data, no path of
# this machine, Unicorn only in a development export).
#
#   build/linux/    gd-ffx.x86_64, libffx_gd.so, data_gd-ffx_linuxbsd_x86_64/
#   build/windows/  gd-ffx.exe, gd-ffx.console.exe, ffx_gd.dll, data_gd-ffx_windows_x86_64/
#
# FFX_GD_FEATURES  the extension's features, as tools/build-extension.sh.
#                  Unset: dev-unicorn, a DEVELOPMENT export (Unicorn, GPL-2.0:
#                  never distribute it). "none": an export without Unicorn,
#                  which stops at the first game function ffx-rust has not
#                  ported yet.
# FFX_RUST_DIR     an ffx-rust checkout, as tools/build-extension.sh.
# GODOT            the Godot 4.7 .NET editor binary (default:
#                  ~/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot); its
#                  .NET export templates must be installed (docs/exporting.md).
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
case "${1:-linux}" in
    linux) platforms=(linux) ;;
    windows) platforms=(windows) ;;
    all) platforms=(linux windows) ;;
    *) echo "usage: tools/export.sh [linux|windows|all]" >&2; exit 2 ;;
esac

# the export templates of this Godot (4.7.2.stable.mono)
version="$("$godot" --version 2>/dev/null | tr -d '\r' | tail -1)"
templates="${XDG_DATA_HOME:-$HOME/.local/share}/godot/export_templates/$(sed -E 's/^([0-9.]+\.[a-z0-9]+\.mono).*/\1/' <<<"$version")"
[[ -f "$templates/version.txt" ]] || {
    echo "export.sh: no export templates at $templates (Godot $version): docs/exporting.md" >&2
    exit 2
}

dev=0
case "${FFX_GD_FEATURES-}" in "" | *dev-unicorn*) dev=1 ;; esac

# the cargo arguments of the build (as tools/build-extension.sh), for the notices
cargo_args=()
remap="--remap-path-prefix=$HOME=~ --remap-path-prefix=$HOME/.cargo=cargo --remap-path-prefix=$HOME/.rustup=rustup"
cmap=("$HOME=~" "$HOME/.cargo=cargo" "$repo=gd-ffx")
if [[ -n "${FFX_RUST_DIR:-}" ]]; then
    ffx="$(cd "$FFX_RUST_DIR" && pwd)"
    for c in ffx-host ffx-bridge ffx-state ffx-phyre ffx-vbf; do
        cargo_args+=(--config "patch.\"https://github.com/yutamago/ffx-rust\".$c.path=\"$ffx/crates/$c\"")
    done
    cmap+=("$ffx=ffx-rust")
fi
# the last matching mapping wins: the most specific last
remap+=" --remap-path-prefix=$repo=gd-ffx"
[[ -n "${ffx:-}" ]] && remap+=" --remap-path-prefix=$ffx=ffx-rust"
case "${FFX_GD_FEATURES-}" in
    "") ;;
    none) cargo_args+=(--no-default-features) ;;
    *) cargo_args+=(--no-default-features --features "$FFX_GD_FEATURES") ;;
esac

echo "== the C# assembly"
# Godot's .NET export needs a solution file; the editor makes one (gitignored),
# a fresh checkout has none: the one Godot would write
sln="$repo/godot/gd-ffx.sln"
if [[ ! -f "$sln" ]]; then
    g="{374952D9-BF3D-4157-8B78-02CEBFCE0636}"
    {
        printf 'Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio 2012\n'
        printf 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "gd-ffx", "gd-ffx.csproj", "%s"\nEndProject\n' "$g"
        printf 'Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n'
        for c in Debug ExportDebug ExportRelease; do printf '\t\t%s|Any CPU = %s|Any CPU\n' "$c" "$c"; done
        printf '\tEndGlobalSection\n\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n'
        for c in Debug ExportDebug ExportRelease; do
            printf '\t\t%s.%s|Any CPU.ActiveCfg = %s|Any CPU\n\t\t%s.%s|Any CPU.Build.0 = %s|Any CPU\n' "$g" "$c" "$c" "$g" "$c" "$c"
        done
        printf '\tEndGlobalSection\nEndGlobal\n'
    } >"$sln"
fi
(cd "$repo/godot" && dotnet build -nologo -v quiet -c ExportRelease gd-ffx.csproj >/dev/null)
# the first run imports the project (and registers the extension); Godot may
# crash on exit right after that first registration (docs/building.md)
[[ -f "$repo/godot/.godot/extension_list.cfg" ]] ||
    "$godot" --headless --editor --quit --path "$repo/godot" >/dev/null 2>&1 || true

work="$(mktemp -d -t gd-ffx-export.XXXXXX)"
trap 'rm -rf "$work"' EXIT
for platform in "${platforms[@]}"; do
    if [[ "$platform" == linux ]]; then
        preset=Linux program=gd-ffx.x86_64 target=x86_64-unknown-linux-gnu rid=linux-x64 prefix_map=-ffile-prefix-map
    else
        preset=Windows program=gd-ffx.exe target=x86_64-pc-windows-msvc rid=win-x64 prefix_map=/clang:-ffile-prefix-map
    fi
    cflags=""
    for m in "${cmap[@]}"; do cflags+=" $prefix_map=$m"; done
    echo "== $platform: the extension (features: ${FFX_GD_FEATURES:-default, dev-unicorn})"
    # Unicorn (dev-unicorn) built from source with the paths remapped, never a
    # prebuilt library found through pkg-config (it keeps its build paths)
    CARGO_TARGET_DIR="$repo/rust/target/export" FFX_GD_RUSTFLAGS="$remap" FFX_GD_CFLAGS="$cflags" \
        UNICORN_NO_PKG_CONFIG=1 "$repo/tools/build-extension.sh" "$platform" build

    out="$repo/build/$platform"
    echo "== $platform: Godot's export -> build/$platform"
    rm -rf "$out"
    mkdir -p "$out"
    log="$work/export-$platform.log"
    "$godot" --headless --path "$repo/godot" --export-release "$preset" "$out/$program" >"$log" 2>&1 || {
        tail -30 "$log" >&2
        echo "export.sh: Godot's export failed" >&2
        exit 1
    }
    # Godot exits 0 on a failed step of an export too
    if grep -q 'ERROR:' "$log"; then
        sed 's/\x1b\[[0-9;]*m//g' "$log" | grep -A2 'ERROR:' | head -40 >&2
        echo "export.sh: Godot's export reported errors" >&2
        exit 1
    fi

    lic="$out/licences"
    mkdir -p "$lic"
    cp "$repo/LICENSE-MIT" "$repo/LICENSE-APACHE" "$lic/"
    gpl=()
    if [[ $dev == 1 ]]; then
        gpl=(--allow-gpl)
        cp "$repo/tools/export/unicorn-NOTICE.txt" "$lic/"
        cp "$(python3 -I "$repo/tools/export-files.py" pkgdir unicorn-engine-sys -- "${cargo_args[@]}")/COPYING" \
            "$lic/unicorn-COPYING"
    fi
    python3 -I "$repo/tools/export-files.py" notices "$lic/THIRD-PARTY.txt" "$target" "${gpl[@]}" -- "${cargo_args[@]}"
    (cd "$work" && "$godot" --headless --script "$repo/tools/godot-licences.gd" -- "$lic" >/dev/null 2>&1) || {
        echo "export.sh: Godot's licence texts failed" >&2
        exit 1
    }
    # the .NET runtime in the data folder: its runtime pack's licence and notices
    deps="$(ls "$out"/data_gd-ffx_*/gd-ffx.deps.json)"
    net="$(grep -o "runtimepack.Microsoft.NETCore.App.Runtime.$rid/[0-9][^\"]*" "$deps" | head -1 | cut -d/ -f2)"
    pack="${NUGET_PACKAGES:-$HOME/.nuget/packages}/microsoft.netcore.app.runtime.$rid/$net"
    [[ -n "$net" && -f "$pack/LICENSE.TXT" ]] || {
        echo "export.sh: the .NET runtime pack's licence not found ($pack)" >&2
        exit 1
    }
    cp "$pack/LICENSE.TXT" "$lic/dotnet-LICENSE.txt"
    cp "$pack/THIRD-PARTY-NOTICES.TXT" "$lic/dotnet-THIRD-PARTY-NOTICES.txt"

    {
        if [[ $dev == 1 ]]; then
            printf '%s\n' "DEVELOPMENT EXPORT: NOT FOR DISTRIBUTION" \
                "This build contains Unicorn (GPL-2.0): licences/unicorn-NOTICE.txt." ""
        fi
        cat "$repo/tools/export/README.txt"
    } >"$work/README.txt"
    if [[ "$platform" == windows ]]; then
        sed 's/\r$//; s/$/\r/' "$work/README.txt" >"$out/README.txt"
    else
        cp "$work/README.txt" "$out/README.txt"
    fi

    devflag=()
    [[ $dev == 1 ]] && devflag=(--dev)
    python3 -I "$repo/tools/export-files.py" check "$out" "$platform" "${devflag[@]}"
    echo "exported: build/$platform ($(du -sh "$out" | cut -f1))"
done
if [[ $dev == 1 ]]; then
    echo "note: a development export (dev-unicorn: Unicorn, GPL-2.0): for your own use, never distribute it."
    echo "      FFX_GD_FEATURES=none tools/export.sh ... builds one without (docs/exporting.md)."
fi
