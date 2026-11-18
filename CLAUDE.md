# gd-ffx

Godot 4 (.NET/C#) frontend for ffx-rust. Plan: ffx-rust's `docs/godot-plan.md`;
here: `docs/architecture.md`, `docs/building.md`, `docs/running.md`,
`docs/exporting.md`, `docs/launcher.md` (the main scene; skipped with
`--game-dir`, `FFX_GAME_DIR`, `--devsave`, `--selftest*`, `--no-launcher`),
`docs/remaster-plan.md`, `docs/remaster-editor.md` (the editor plugin,
`godot/addons/ffx_remaster`; R2's textures, materials and render path),
`docs/remaster-materials.md` (R2b, the material workbench: planned).

## Rules

- No game data in git: no game files, extracted or converted assets,
  decompilation output, saves, nor textures or maps made from the game's
  (`godot/remaster/textures/` is gitignored: remaster projects are packs). The game is read from the player's copy at
  run time; the game folder is read-only, saves go to gd-ffx's own sandbox.
- Licence MIT OR Apache-2.0; no GPL dependencies in release builds (gdext is
  MPL-2.0: fine; Unicorn only behind the `dev-unicorn` feature, off by default).
- Commit as Yutamago <12561505+yutamago@users.noreply.github.com>. No real
  names, user names or user paths in commits, code or docs. The second
  machine is "PC2" in docs.
- Push only to github.com/yutamago/gd-ffx (public since 2026-11-16:
  everything pushed is published). Never `git push --all`, never force-push main.
- Changes gd-ffx needs in ffx-rust: a branch in its own worktree, a PR on
  ffx-rust, reviewed, merged; keep its gates green (fmt, clippy
  `-D warnings` in both ffx-host feature sets, ffx-host tests,
  `tools/check-hostptr.py`, `tools/check-licences.py`) and every replay
  unchanged.
- Running the game counts as playing: headless/background only, no visible
  windows unless the user asks. Kill processes by exact PID.
- Memory: never above 80 % of the machine's (WSL crashes when memory and
  swap run out). Every heavy command (cargo builds and tests, Godot runs,
  exports) goes through `tools/capped.sh`: one shared systemd user slice,
  `gdffx.slice` (MemoryMax 24G, swap 2G; docs/building.md "Memory"), cargo
  at 8 jobs. Run at most two heavy jobs at once.
- Storage is limited: remove worktrees and branches once merged, downloads
  once installed, temp folders after tests; never ones still in use.

## Commands

```bash
export FFX_RUST_DIR=/path/to/ffx-rust            # optional: a local ffx-rust
tools/build-extension.sh [linux|windows] [build|clippy|test]
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/selftest.sh    # headless check (start-up opening natively, snapshots, Start)
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/sweep-devsaves.sh   # the map check on 10 saves covering every kind of map (`all`: all 110, for importer/shader/loading changes and releases)
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/bench.sh [besaid besaid-walk lchb battle battle-cloth]  # G5: frame times, views' CPU, GC, hitches (docs/running.md "Benchmark")
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-besaid --check-autosave --devsave=7)  # G4: a real autosave read back by the save list
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-besaid --devsave=7)   # G2 check; G5: the walk's map change held, no stand-ins (--transition-shots=DIR under Xvfb); Tidus's cloth placed and moving
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-remake --devsave=7)  # G6: a remade c001 posed by the game
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-remake-map --devsave=7)  # G6: the remade Besaid beach example
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-overrides --devsave=7)  # R1: Besaid's example overrides (remove, hide, move, replace, add) in the running game; the catalogue entry
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-materials --devsave=7)  # R2: a replaced texture drawn classic, a PBR material remastered, F7, a live replacement (--shot=F.png under Xvfb: F.png, F-remastered.png)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-lighting --devsave=7)  # R3: the remastered lighting on Besaid (defaults from the game's look, a lighting file, a volume, VoxelGI, quality, F7); the colour check needs a renderer:
FFX_GAME_DIR=... FFX_SHOT_TEST="--selftest-lighting --selftest-timeout=400" tools/compare-shot.sh - OUT_DIR  # R3: the colour check, classic against remastered (Compatibility; compare-shot-win.sh: the GPU)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-menu --devsave=7)        # G4: the field menu panel (party, gil, time), then Items (--menu-screen=status|abilities|overdrive|sphere-grid|config)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-battle --devsave=7 --battle=3,0,0)  # G3/G4: battle, HUD, attack, effects, a damage number per hit (--to-results: on to the results screen)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-ux --devsave=7)   # G5: choice cursor, ESC menu (halt, Change parameters) + options, game speed, F1/F3 boosters, pause
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-save --devsave=7 --documents=/tmp/gd-ffx-sl)  # G5: the save screen (saveload_*.swf natively): save, Yes/No, overwrite No, cancel
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-load --documents=/tmp/gd-ffx-sl)  # G5: start-up opening, title, the load screen, load (after the save check; rm the folder after)
# any check: --check-flash (every Flash movie the game opened/showed has a native screen); --flash-log
FFX_GAME_DIR=... tools/ux-shots.sh OUT_DIR                 # G5: offscreen pictures of the native screens (Xvfb)
FFX_GAME_DIR=... FFX_SHOT_TEST="--selftest-overrides --selftest-timeout=90 [--no-overrides]" tools/compare-shot.sh - OUT_DIR  # R1: the overrides' after (before) picture
FFX_GAME_DIR=... tools/remaster-editor-check.sh [OUT_DIR]   # R1/R2: the editor plugin headless on Besaid beach (open, move, hide, remove, replace, add, undo, save, reopen, status; where-used, a replaced texture, a generated material, render path remastered, the batch), then the game on save 7 with what it saved and with its textures, then R1a's example round trip (FFX_EDITOR_SHOT=1: under Xvfb, pictures); rm OUT_DIR after
(cd godot && godot --headless --path . -- --selftest-saves --no-game)  # G4: the save list reader (no game)
(cd godot && godot --headless --path . -- --selftest-mods --no-game)   # G6: mod packs, load order, registry, overrides layered over packs (no game)
(cd godot && godot --headless --path . -- --selftest-launcher --no-game)   # G6: the launcher (no game; fake folders in /tmp)
(cd godot && dotnet build)
(cd rust && cargo run -p ffx-state-cs)                    # regenerate the C# state bindings after an ffx-state change
tools/capped.sh tools/export.sh [linux|windows|all]       # standalone program in build/<platform>/ (docs/exporting.md)
FFX_GD_FEATURES=dev-unicorn tools/capped.sh tools/export.sh all  # with Unicorn: development only, never distribute
FFX_GAME_DIR=... tools/capped.sh build/linux/gd-ffx.x86_64 --headless -- --selftest --documents=/tmp/gd-ffx-docs   # the export's check
```

The extension has no default features (since 2026-11-17): no Unicorn, no
Ruffle (`swf`); every screen is drawn natively by Godot, the Flash ones from
the game's calls on its movies (docs/architecture.md, "The Flash screens"). An export with
dev-unicorn contains Unicorn (GPL-2.0): never distribute it. Exports need Godot's .NET export templates
(docs/exporting.md).
