# gd-ffx

Godot 4 (.NET/C#) frontend for ffx-rust. Plan: ffx-rust's `docs/godot-plan.md`;
here: `docs/architecture.md`, `docs/building.md`, `docs/running.md`,
`docs/exporting.md`.

## Rules

- No game data in git: no game files, extracted or converted assets,
  decompilation output, saves. The game is read from the player's copy at
  run time; the game folder is read-only, saves go to gd-ffx's own sandbox.
- Licence MIT OR Apache-2.0; no GPL dependencies in release builds (gdext is
  MPL-2.0: fine; Unicorn only behind the `dev-unicorn` feature).
- Commit as Yutamago <12561505+yutamago@users.noreply.github.com>. No real
  names, user names or user paths in commits, code or docs. The second
  machine is "PC2" in docs.
- Push only to github.com/yutamago/gd-ffx (private until a first playable
  map). Never `git push --all`, never force-push main.
- Changes gd-ffx needs in ffx-rust: a branch in its own worktree, a PR on
  ffx-rust, reviewed, merged; keep its gates green (fmt, clippy
  `-D warnings` in both ffx-host feature sets, ffx-host tests,
  `tools/check-hostptr.py`, `tools/check-licences.py`) and every replay
  unchanged.
- Running the game counts as playing: headless/background only, no visible
  windows unless the user asks. Kill processes by exact PID.

## Commands

```bash
export FFX_RUST_DIR=/path/to/ffx-rust            # optional: a local ffx-rust
tools/build-extension.sh [linux|windows] [build|clippy|test]
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/selftest.sh    # headless check (opening, snapshots, Start)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-besaid --devsave=7)   # G2 check
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-remake --devsave=7)  # G6: a remade c001 posed by the game
(cd godot && godot --headless --path . -- --selftest-mods --no-game)   # G6: mod packs, load order, registry (no game)
(cd godot && dotnet build)
(cd rust && cargo run -p ffx-state-cs)                    # regenerate the C# state bindings after an ffx-state change
tools/export.sh [linux|windows|all]                       # standalone program in build/<platform>/ (docs/exporting.md)
FFX_GD_FEATURES=none tools/export.sh all                  # without Unicorn: the only kind to give to others
FFX_GAME_DIR=... build/linux/gd-ffx.x86_64 --headless -- --selftest --documents=/tmp/gd-ffx-docs   # the export's check
```

An export with the default features (dev-unicorn) contains Unicorn (GPL-2.0):
never distribute it. Exports need Godot's .NET export templates
(docs/exporting.md).
