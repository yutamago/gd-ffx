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
| G3 | Sound through Godot's audio engine; effects; battles with classic content | |
| G4 | Native Godot UI for the menus | |
| G5 | The whole game with classic content on Windows, Linux, the Steam Deck, macOS | |
| G6 | Remake tooling: replacement by id, editor workflow, mod packs, launcher | |

Godot shows the game's own maps and characters, imported on your machine
from your copy (docs/importer.md), posed and placed by the game through the
game-state API at the display's frame rate, with the game's camera and a
debug overlay of the live state. Your keyboard, mouse and pads go to the
game, and the game's own sound mix plays through Godot's audio. Menus, text
boxes and effects are still to come.

## Building and running

[docs/building.md](docs/building.md) and [docs/running.md](docs/running.md);
a standalone program for Windows and Linux (and the Steam Deck):
[docs/exporting.md](docs/exporting.md). In short, on Linux:

```bash
tools/build-extension.sh                          # rust/ffx-gd
FFX_GAME_DIR=/path/to/FINAL\ FANTASY\ X-X2\ HD\ Remaster tools/selftest.sh
tools/export.sh all                               # build/linux, build/windows (development exports)
```

## Licence

gd-ffx's code is licensed under either of [Apache License, Version 2.0](LICENSE-APACHE)
or [MIT license](LICENSE-MIT), at your option, like ffx-rust.

FINAL FANTASY is a registered trademark of Square Enix Holdings Co., Ltd.
gd-ffx is a fan project, not affiliated with or endorsed by Square Enix.
