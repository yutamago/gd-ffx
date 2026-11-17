# gd-ffx

A Godot 4 frontend for [ffx-rust](https://github.com/yutamago/ffx-rust), the
Rust rewrite of FINAL FANTASY X (the Steam HD Remaster). The aim is a
community-driven remake: ffx-rust stays the game (its logic, event scripts,
battles, saves, 30 updates a second), and Godot becomes everything the player
sees, hears and touches.

**You need your own copy of the game.** gd-ffx contains nothing of FINAL
FANTASY X: no game files, no extracted or converted assets, no code from the
original. It reads the game from your installed copy (the Steam HD Remaster)
when it runs, and only reads it. Your saves are left alone: the game's
settings and saves go to a separate folder of gd-ffx's own.

## How it works

```
 Godot 4 (.NET, C#)                     ffx-rust (Rust, a GDExtension)
 +------------------------------+       +-------------------------------+
 | scenes, UI, audio (C#)       | <---- | the game: logic, scripts,     |
 |                              | state |   battles, menus, saves       |
 | input, window                | ----> |   at 30 updates a second      |
 +------------------------------+       | GodotPlatform (window, input) |
                                        +-------------------------------+
```

- `rust/ffx-gd`: ffx-rust loaded into Godot as a GDExtension (godot-rust).
  It plugs into ffx-rust's platform seam as `GodotPlatform`, boots the game
  from your game folder and runs it on its own thread.
- `godot/`: the Godot project (C#).

More: [docs/architecture.md](docs/architecture.md).

## Status

| | Milestone | Status |
|---|---|---|
| G0 | ffx-rust as a GDExtension; `GodotPlatform` (window, input); the game runs headless inside Godot at 30 updates a second; a debug view of the state | done |
| G1 | The game-state API: world, camera, actors, input; snapshots and interpolation; C# bindings | done |
| G2 | The classic importer (maps, models, textures, animations, cameras) and the first playable map | done: Besaid beach from the archive, characters posed by the game, walkable to the next map |
| G3 | Sound through Godot's audio engine; effects; battles with classic content | done: the game's mix through Godot (about 0.11 s behind); battles with the classic battle map, a HUD (HP/MP, turn order, damage numbers); the game's own effects drawn from the geometry it builds each frame with the game's blends and samplers (main, DTT and after-post passes), the screen fade, the map's glow, the screen distortion |
| G4 | Native Godot UI for the menus | done for the field, battle and PC screens: message boxes with the live choice cursor, the battle command menu and target selection, the battle results, the main menu (party, items, status, abilities, overdrive, sphere grid, config), the save and load lists, the pause screen, the Esc menu (incl. Change parameters), the F1-F4 boosters' badges and tips, gd-ffx's options and FPS overlays (the game keeps the logic, Godot draws it; no Ruffle); every Flash screen the game uses has a native counterpart: the start-up opening, the autosave prompt, save and load, tips, free camera and time stop (`--check-flash` in the sweep) |
| G5 | The whole game with classic content on Windows, Linux, the Steam Deck, macOS | Linux and Windows exports (no Unicorn, no Ruffle by default), checked on the GPU (D3D12/Vulkan, Forward+: Besaid 8.9/255 against the original); all 110 developer saves across the game pass the sweep (tools/sweep-devsaves.sh) with maps, 2D rooms, characters, cloth, objects, effects; clean map transitions; a Steam Deck quality preset; macOS left out for now (decided 2026-11-16); the repository is public |
| G6 | Remake tooling: replacement by id, editor workflow, mod packs, launcher | done as a base: remade maps, characters and effects replace classic ones by the game's ids, mod packs in a load order (docs/remake.md), the launcher (docs/launcher.md); a remade Besaid beach and effect as examples |
| R1-R8 | The remaster: editor plugins, per-part edits, textures and PBR, lighting, a remaster wizard, characters, effects, quality (docs/remaster-plan.md) | R1a done (2026-11-17): stable part IDs, the map catalogue, per-map overrides applied in the game; next R1b, the editor plugin |

Godot shows the game's own maps and characters, imported on your machine
from your copy (docs/importer.md), lit and coloured with the game's own
shader math (fog, water, the map's lights and glow), posed and placed by the
game through the game-state API at the display's frame rate, with the
game's camera, its moving map objects and its effects. Your keyboard, mouse
and pads go to the game, the game's own sound mix plays through Godot's
audio, and the game's text boxes and menus are drawn as native Godot UI.

## Building and running

[docs/building.md](docs/building.md) and [docs/running.md](docs/running.md);
a standalone program for Windows and Linux (and the Steam Deck):
[docs/exporting.md](docs/exporting.md). In short, on Linux:

```bash
tools/build-extension.sh                          # rust/ffx-gd
FFX_GAME_DIR=/path/to/FINAL\ FANTASY\ X-X2\ HD\ Remaster tools/selftest.sh
(cd godot && godot --path .)                      # the launcher: choose the game folder, Start
tools/export.sh all                               # build/linux, build/windows (development exports)
```

## Licence

gd-ffx's code is licensed under either of [Apache License, Version 2.0](LICENSE-APACHE)
or [MIT license](LICENSE-MIT), at your option, like ffx-rust.

FINAL FANTASY is a registered trademark of Square Enix Holdings Co., Ltd.
gd-ffx is a fan project, not affiliated with or endorsed by Square Enix.
