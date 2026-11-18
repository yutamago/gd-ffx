# gd-ffx

Godot 4 (.NET/C#) frontend for ffx-rust. Plan: ffx-rust's `docs/godot-plan.md`;
here: `docs/architecture.md`, `docs/building.md`, `docs/running.md`,
`docs/exporting.md`, `docs/launcher.md` (the main scene; skipped with
`--game-dir`, `FFX_GAME_DIR`, `--devsave`, `--selftest*`, `--no-launcher`),
`docs/remaster-plan.md`, `docs/remaster-editor.md` (the editor plugin,
`godot/addons/ffx_remaster`; R2's textures, materials and render path;
R5's game data: walk mesh, triggers, cameras, the alignment check; R6's
Characters dock; R4's Wizard dock, "The remaster wizard"),
`docs/remaster-materials.md` (R2b, the material workbench: phase 1, the
Materials tab, phase 2, the GPU pipeline and Materialize's concepts, and
phase 3, profiles and the queue, done; phases 4 to 6 planned; the starter
profiles in `godot/remaster/profiles/`, committed); R6's remade characters (retargeting,
materials, the stand-in): `docs/remake.md`; R7a's effects by id, replaced
sounds and movies: `docs/remake.md` ("Effects"), `docs/remaster-editor.md`
("Effects, audio and movies"), `docs/architecture.md` ("Replacing music,
sound effects, voice and movies"); F8 turns them off and on. R7b's 2D rooms
rebuilt in 3D and UI art by id: `docs/remake.md` ("Rebuilt 2D rooms"),
`docs/remaster-editor.md` ("2D rooms rebuilt in 3D", "UI art"); F7 compares
a rebuilt room with the classic layers, F8 the UI art. R8's quality tools:
`docs/remaster-editor.md` ("Before/after renders", "The status dashboard"),
`docs/running.md` ("Regression pictures", "Budgets", "The GPU checks"),
`docs/building.md` ("Privacy and the quick gates").

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
- Before every push: `tools/check-all.sh` (the privacy check, `dotnet build`
  0 warnings, fmt and clippy, the no-game self-checks; with `FFX_GAME_DIR` the
  start-up and Besaid). `tools/check-privacy.py` alone in a second: no home
  folder, user name or e-mail address in tracked files (docs/building.md,
  "Privacy and the quick gates").
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
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/sweep-devsaves.sh --pictures   # R8: the sweep's regression pictures under Xvfb, held at one update, against ~/.cache/gd-ffx/sweep-baseline (`tools/sweep-devsaves.sh accept` takes the last run as the baseline)
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/budget.sh [windows|linux] [besaid ...]  # R8: budgets classic/remastered at the Deck preset and desktop (Windows export on the GPU; linux: llvmpipe, no verdict); the dashboard's table
FFX_GAME_DIR=~/steam-games/FFX-X2-HD [FFX_REF_BESAID=REF.bmp] tools/gpu-checks.sh [OUT_DIR]   # R8: the GPU checks in one go on the Windows export (classic score, lighting colour, materialize, character, rebuild 14, effects), a summary table
tools/check-all.sh                                         # R8: the quick gates before a push (privacy, dotnet 0 warnings, fmt, clippy, no-game self-checks; FFX_GAME_DIR: start-up, Besaid)
python3 -I tools/check-privacy.py                          # R8: no home folder, user name or e-mail of anyone in the tracked files
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-besaid --check-autosave --devsave=7)  # G4: a real autosave read back by the save list
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-besaid --devsave=7)   # G2 check; G5: the walk's map change held, no stand-ins (--transition-shots=DIR under Xvfb); Tidus's cloth placed and moving
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-remake --devsave=7)  # G6: a remade c001 posed by the game
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-remake-map --devsave=7)  # G6: the remade Besaid beach example
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-character --devsave=7)  # R6: the original stand-in humanoid retargeted on Tidus while he walks (bone directions, joints, game cloth, spring bones, face table, presets; --shot=F.png under Xvfb)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-overrides --devsave=7)  # R1: Besaid's example overrides (remove, hide, move, replace, add) in the running game; the catalogue entry
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-materials --devsave=7)  # R2: a replaced texture drawn classic, a PBR material remastered, F7, a live replacement (--shot=F.png under Xvfb: F.png, F-remastered.png)
(cd godot && godot --headless --path . -- --selftest-materialize --no-game)   # R2b phase 2: the material pipeline on a synthetic texture: the CPU only headless; under Xvfb the GPU's parity with it and its times
FFX_GAME_DIR=... FFX_SHOT_TEST="--selftest-materialize --selftest-timeout=600" tools/compare-shot-win.sh - OUT_DIR  # R2b: on a Besaid texture on the GPU (Windows export, Vulkan): parity, a slider's time, band rebuilds, bakes at 1024/2048/4096; godot.png the maps
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-lighting --devsave=7)  # R3: the remastered lighting on Besaid (defaults from the game's look, a lighting file, a volume, VoxelGI, quality, F7); the colour check needs a renderer:
FFX_GAME_DIR=... FFX_SHOT_TEST="--selftest-lighting --selftest-timeout=400" tools/compare-shot.sh - OUT_DIR  # R3: the colour check, classic against remastered (Compatibility; compare-shot-win.sh: the GPU)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-alignment --devsave=7)  # R5: the game's data (walk mesh, triggers, entrances) against the running game, the capture for the editor, the alignment check (an aligned patch, a sunken one, a misplaced box)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-materialize --materialize-profiles=/tmp/sheets)  # R2b: the starter profiles on their kinds' textures (colour check, roughness, metallic, tilt; contact sheets, never in git)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-effects --devsave=7 --remaster-effects=/tmp/fx)  # R7a: Besaid's effects by id, the example remake on the save sphere's glow, its classic draws left out, F8, the recording (--shot=F.png under Xvfb)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-audio --devsave=7)  # R7a: the sea effect and the deck battle's music replaced by generated stand-ins, the game's silenced in its mix, F8
tools/make-test-movie.sh /tmp/test.ogv 40                  # R7a: an original test video (Godot's movie writer, Xvfb)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-movie --devsave=22 --movie-video=/tmp/test.ogv)  # R7a: Luca's movie replaced, its subtitle, the game's movie sound silenced, F8
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-wizard-classifier --devsave=7)  # R4: the wizard's classifier on its hand-labelled set (accuracy, leave one area out, per kind, confusion matrix)
FFX_GAME_DIR=... FFX_SHOT_TEST="--selftest-wizard --selftest-timeout=500" tools/compare-shot.sh - OUT_DIR  # R4: the wizard's pass in the game on Besaid, its colour check (--wizard-expect=FILE: the editor's accepted pass; compare-shot-win.sh: the GPU)
FFX_GAME_DIR=... tools/remaster-wizard-check.sh [OUT_DIR]   # R4: the wizard in the editor under Xvfb (stage, review, accept, undo, batch pause/resume), the game with it, a headless batch across sessions, the classifier (step 7 of remaster-editor-check.sh)
(cd godot && FFX_GAME_DIR=... godot --headless --editor --path . -- --remaster-wizard --maps=bsil00,slik08 [--wizard-resume] [--wizard-accept])  # R4: the wizard's batch headless (no pictures); state in res://remaster/wizard/
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-rebuild --devsave=14)  # R7b: ptkl14 rebuilt in 3D (projected layer planes, the floor, the game's camera, F7; also 12, 52); pictures: FFX_SHOT_TEST="--selftest-rebuild --selftest-timeout=200" tools/compare-shot.sh - OUT_DIR 14
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-ui-art --devsave=7)  # R7b: UI art replaced on the save screen (cursor, a portrait; --ui-font=F.ttf), F8
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-sky --devsave=22)    # R3's sky rule: no ground kept classic as sky (29, 7; --sky-level=/map/lchb/lchb00/)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-menu --devsave=7)        # G4: the field menu panel (party, gil, time), then Items (--menu-screen=status|abilities|overdrive|sphere-grid|config)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-battle --devsave=7 --battle=3,0,0)  # G3/G4: battle, HUD, attack, effects, a damage number per hit (--to-results: on to the results screen)
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-ux --devsave=7)   # G5: choice cursor, ESC menu (halt, Change parameters) + options, game speed, F1/F3 boosters, pause
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-save --devsave=7 --documents=/tmp/gd-ffx-sl)  # G5: the save screen (saveload_*.swf natively): save, Yes/No, overwrite No, cancel
(cd godot && FFX_GAME_DIR=... godot --headless --path . -- --selftest-load --documents=/tmp/gd-ffx-sl)  # G5: start-up opening, title, the load screen, load (after the save check; rm the folder after)
# any check: --check-flash (every Flash movie the game opened/showed has a native screen); --flash-log
FFX_GAME_DIR=... tools/ux-shots.sh OUT_DIR                 # G5: offscreen pictures of the native screens (Xvfb)
FFX_GAME_DIR=... FFX_SHOT_TEST="--selftest-overrides --selftest-timeout=90 [--no-overrides]" tools/compare-shot.sh - OUT_DIR  # R1: the overrides' after (before) picture
FFX_GAME_DIR=... tools/remaster-editor-check.sh [OUT_DIR]   # R1/R2/R3/R2b/R5/R6/R7a/R7b/R4: the editor plugin headless on Besaid beach (open, move, hide, remove, replace, add, undo, save, reopen, status; where-used, a replaced texture, materials by a starter profile, render path remastered; lighting; the Materials tab: queue, steps, a drag, latency, import, skip, switch, save, undo; profiles, exceptions, Apply to queue (cancel, undo), a changed profile made again, an R2-era entry, a pack's profile; the game's data, the birds from the game's capture, the alignment check; the 2D-layer map ptkl14), then the game on save 7 with what it saved and with its textures, then R1a's example round trip, then R6's characters (the list, c001 with its motions, the stand-in assigned, auto-mapped, one bone by hand, save, undo/redo, reopen; the game with that rig), then R7a's Media dock (the recorded effects, a remake assigned, the sounds named and decoded, stand-ins, a movie replaced; undo/redo, reopen; the game with its files), then R7b (Rebuild in 3D on ptkl14: references only, alignment, built, saved; the UI Art dock: list, assign, undo/redo, preview; the game with them), then R8 (the renders batch on bsil00, ptkl14, slik08 under Xvfb and again: kept; the status dashboard's counts, thumbnails, filters, a budgets fixture; the HTML report and its links) (FFX_EDITOR_SHOT=1: under Xvfb, pictures); rm OUT_DIR after
(cd godot && FFX_GAME_DIR=... xvfb-run -a godot --editor --display-driver x11 --rendering-driver opengl3 --audio-driver Dummy --path . -- --remaster-renders --maps=a,b|changed|all [--renders-dir=DIR] [--renders-resume])  # R8: before/after renders of maps (offscreen, resumable; user://remaster/renders), the colour check per map
FFX_GAME_DIR=... tools/remaster-report.sh OUT_DIR [-- EDITOR_ARGS]   # R8: the status dashboard as an HTML report with the renders' pictures (outside git), its links checked
(cd rust && cargo run --release -p ffx-import -- gamedata bsil00)   # R5: a map's game data as JSON (walk meshes, scene cameras, entrances, workers): reading aid, keep out of git
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
