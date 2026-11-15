# The launcher (G6)

`scenes/launcher.tscn` (`godot/src/Launcher/Launcher.cs`) is the project's
main scene: the first screen of a normal start. It sets up a run, then
hands over to the game (`scenes/main.tscn`).

## What it shows

- **The game folder**: the folder with `FFX.exe` and `data/FFX_Data.vbf`
  (the Steam HD Remaster's install folder). Typed in, or chosen with
  "Browse..." (a folder dialog, the system's own where Godot has one). It
  is checked for those two files (only their presence; nothing is read),
  and Start stays off until it passes. A folder that passes is remembered
  in the user settings file (below), so the next start has it filled in.
- **The language**: the game's `Language` from its `GameSetting.ini` in
  gd-ffx's documents folder (docs/running.md). Shown only: the game's PC
  menu (Esc) changes it. Before the first start there is no file yet and
  the game starts in Japanese.
- **The mod packs** of `user://mods/` and of each `--mods=DIR`, one list a
  folder, first to last (later packs win, docs/remake.md): a check box
  enables or disables a pack, Up and Down move it. Each change is written
  to that folder's `load_order.txt` at once.
- **The classic cache** (`user://classic`, or `--classic=PATH`): its size,
  and "Clear cache", which deletes its `map/` and `chr/` folders (the
  importer's output, made from your copy; imported again when needed) and
  nothing else.
- **Start**, and **Start at developer save N** (1..110, for testing:
  docs/running.md, `--devsave`).

## The user settings file

`user://settings.cfg` (Godot's user data folder for gd-ffx; `--settings=PATH`
after `--` for another file), a Godot `ConfigFile`:

```ini
[game]
dir="/path/to/the/game"
```

The game folder is the first found of `--game-dir=PATH`, `FFX_GAME_DIR`,
the project setting `ffx/game/game_dir`, then this file's `[game] dir`.
The launcher starts the game from the folder it shows, whatever the source.

## The load order file

`load_order.txt` in a mod folder: one pack file name a line (`.pck`
optional, any case), first to last; blank lines and `#` comments are
skipped; packs not listed come after the listed ones, alphabetically. A
pack switched off in the launcher stays at its place as

```
# disabled: tidus-hd.pck
```

which `ModLoader` reads as "keep it, do not mount it" (to anything else it
is a comment). The launcher writes the whole file:

```
# gd-ffx mod packs, first to last (later packs win); "# disabled: NAME" keeps a pack but does not load it
tidus-hd.pck
# disabled: besaid-beach.pck
```

## Handing over

Start calls `GameHost.Start(gameDir, devsave)`, which first mounts the
enabled packs in their order (`ModLoader.MountAll`), then boots the game,
and then the launcher changes the scene to `scenes/main.tscn`. The packs
are mounted only then, not at start-up: a pack mounted with
`ProjectSettings.LoadResourcePack` cannot be taken out again, so the
launcher's choices must come before any mounting, and mounting before
`main.tscn` loads lets a pack replace its files. `ModLoader` (an autoload)
still reads the base registry at start-up; `GameHost` (an autoload) boots
nothing until `Start`.

## Skipping it

The launcher hands over at once (no screen), as gd-ffx started before it
existed, when the command line or environment already says what to run:

- `--game-dir=PATH` or `FFX_GAME_DIR`;
- `--devsave=N`;
- any `--selftest...` (except `--selftest-launcher`);
- `--no-launcher` (the game folder from the project setting or the
  settings file).

All self-tests and `tools/selftest.sh` run as before. A scene run directly
(`scenes/main.tscn` from the editor, F6) starts the game itself (its packs
are then mounted after that scene loaded).

## The self-test

```bash
cd godot
godot --headless --path . -- --selftest-launcher --no-game
```

No game needed. It makes a temporary folder with a fake game folder (empty
files named `FFX.exe` and `data/FFX_Data.vbf`; nothing of the game), one
without the archive, three small packs built with `PCKPacker`, a
`GameSetting.ini` with `Language=de` and a classic cache, and points the
settings file and the mod folders there. Then it drives the launcher's
controls: the folder without the archive is refused and not remembered,
the fake one is accepted and remembered in the settings file; a pack is
switched off and another moved up, and `load_order.txt` says so (and reads
back the same); the language shows German; the cache is cleared (a file
beside `map/` and `chr/` is kept); Start hands over to `scenes/main.tscn`
with the enabled packs mounted in the chosen order and the disabled one
not, and, with `--no-game`, no game booted. It deletes its folder and
prints `ffx selftest: PASS` or `FAIL` (exit status 0 or 1).
