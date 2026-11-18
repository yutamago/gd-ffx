#!/usr/bin/env bash
# The quick gates before a push (R8; CLAUDE.md): one after another, each heavy
# step through tools/capped.sh, nothing on screen, a line each and a summary.
#  1. privacy: tools/check-privacy.py (no path, user name or e-mail address of a
#     machine or a person in the tracked files);
#  2. dotnet build of the Godot project: 0 warnings, 0 errors;
#  3. rust: cargo fmt --check, clippy -D warnings (tools/build-extension.sh linux clippy);
#  4. the headless self-checks that need no renderer and no game: the save list
#     (--selftest-saves), mod packs (--selftest-mods), the launcher
#     (--selftest-launcher), the material pipeline on the CPU (--selftest-materialize);
#  5. with FFX_GAME_DIR: the start-up check (tools/selftest.sh) and Besaid
#     (--selftest-besaid --devsave=7), headless.
#
#   tools/check-all.sh
#   CHECK_ALL_SKIP=rust,game tools/check-all.sh    # leave steps out (privacy, dotnet, rust, godot, game)
#
# Logs in $CHECK_ALL_OUT (default /tmp/gd-ffx-check-all), removed when every step passed.
set -u
repo="$(cd "$(dirname "$0")/.." && pwd)"
godot="${GODOT:-$HOME/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot}"
out="${CHECK_ALL_OUT:-/tmp/gd-ffx-check-all}"
skip=",${CHECK_ALL_SKIP:-},"
rm -rf "$out"
mkdir -p "$out"
results=()
fail=0
step() { # NAME RESULT NOTE
    results+=("$(printf '%-22s %-5s %s' "$1" "$2" "$3")")
    [[ "$2" == PASS || "$2" == SKIP ]] || fail=1
    printf '%-22s %-5s %s\n' "$1" "$2" "$3"
}
skipped() { [[ "$skip" == *",$1,"* ]]; }

# 1. privacy
if skipped privacy; then step privacy SKIP ""; else
    if python3 -I "$repo/tools/check-privacy.py" >"$out/privacy.log" 2>&1; then
        step privacy PASS "$(tail -1 "$out/privacy.log" | cut -c16-)"
    else
        step privacy FAIL "$(grep -c '^check-privacy: .*:' "$out/privacy.log") finding(s), see $out/privacy.log"
        head -5 "$out/privacy.log"
    fi
fi

# 2. dotnet build: 0 warnings
if skipped dotnet; then step dotnet SKIP ""; else
    (cd "$repo/godot" && "$repo/tools/capped.sh" dotnet build -nologo gd-ffx.csproj) >"$out/dotnet.log" 2>&1
    code=$?
    warn=$(grep -oE '^ +[0-9]+ Warning\(s\)' "$out/dotnet.log" | tail -1 | tr -dc 0-9)
    err=$(grep -oE '^ +[0-9]+ Error\(s\)' "$out/dotnet.log" | tail -1 | tr -dc 0-9)
    if [[ $code -eq 0 && "${warn:-x}" == 0 && "${err:-x}" == 0 ]]; then step "dotnet build" PASS "0 warnings, 0 errors"
    else step "dotnet build" FAIL "exit $code, ${warn:-?} warning(s), ${err:-?} error(s), see $out/dotnet.log"; fi
fi

# 3. rust: fmt and clippy
if skipped rust; then step rust SKIP ""; else
    if (cd "$repo/rust" && cargo fmt --all -- --check) >"$out/fmt.log" 2>&1; then step "cargo fmt" PASS ""
    else step "cargo fmt" FAIL "see $out/fmt.log"; fi
    if "$repo/tools/capped.sh" "$repo/tools/build-extension.sh" linux clippy -- -D warnings >"$out/clippy.log" 2>&1; then
        step clippy PASS "-D warnings"
    else
        step clippy FAIL "see $out/clippy.log"
    fi
fi

# 4. the headless self-checks without a game or a renderer
run_check() { # NAME TIMEOUT ARGS...
    local name=$1 t=$2
    shift 2
    (cd "$repo/godot" && timeout "$t" "$repo/tools/capped.sh" "$godot" --headless --path . -- "$@") >"$out/$name.log" 2>&1
    local code=$?
    local line
    line=$(grep -E '^ffx selftest: (PASS|FAIL)' "$out/$name.log" | tail -1 | cut -c15-140)
    if [[ $code -eq 0 && "$line" == PASS* ]]; then step "$name" PASS "${line#PASS }"
    else step "$name" FAIL "exit $code ${line:-no result}, see $out/$name.log"; fi
}
if skipped godot; then step "godot checks" SKIP ""; else
    run_check saves 200 --selftest-saves --no-game
    run_check mods 200 --selftest-mods --no-game
    run_check launcher 200 --selftest-launcher --no-game
    run_check materialize 400 --selftest-materialize --no-game
fi

# 5. with the game: the start-up and Besaid, headless
if skipped game || [[ -z "${FFX_GAME_DIR:-}" ]]; then step "game checks" SKIP "${FFX_GAME_DIR:+skipped}${FFX_GAME_DIR:-no FFX_GAME_DIR}"; else
    if "$repo/tools/selftest.sh" >"$out/selftest.log" 2>&1; then
        step "start-up (selftest.sh)" PASS "$(grep -E '^ffx selftest: PASS' "$out/selftest.log" | tail -1 | cut -c20-120)"
    else
        step "start-up (selftest.sh)" FAIL "see $out/selftest.log"
    fi
    run_check besaid 300 --selftest-besaid --devsave=7 --documents="$out/docs-besaid" --selftest-timeout=200
    rm -rf "$out/docs-besaid"
fi

echo
echo "check-all summary:"
printf '  %s\n' "${results[@]}"
if [[ $fail -eq 0 ]]; then
    echo "check-all: PASS"
    rm -rf "$out"
    exit 0
fi
echo "check-all: FAIL (logs in $out)"
exit 1
