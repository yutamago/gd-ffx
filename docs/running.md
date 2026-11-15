# Running

## The game folder

gd-ffx boots the game from your installed copy: the folder with `FFX.exe`
and `data/` (the Steam HD Remaster's install folder). It only reads it. It is
the first found of:

1. `--game-dir=PATH` on the command line, after `--`;
2. the environment variable `FFX_GAME_DIR`;
3. the project setting `ffx/game/game_dir`.

The game's settings (`GameSetting.ini`) and saves go to `--documents=PATH`,
else the project setting `ffx/game/documents_dir` (default
`user://documents`, Godot's user data folder for gd-ffx). It is never your
own documents folder, so the original's settings and saves are untouched.

## The debug view (G0)

The main scene, `scenes/debug_view.tscn`, boots the game and shows its state
live: updates (the game runs 30 a second), frames, game time, the map and the
one before it, the entrance, the story progress, the running event's frame
count and the keys held.

Input goes to the game as on the original:

| Keys | Game |
|---|---|
| arrows | directions |
| C, Enter | confirm (circle) |
| X, Backspace | cancel (cross) |
| V | menu (triangle) |
| Z | square |
| B | Start |
| N | Select |
| S / D, PgUp / PgDn | L1 / R1, L2 / R2 |
| Esc | the PC menu |

Pads are the game's XInput pads (the first four Godot joypads connected at the
start). The keyboard and mouse reach the game only while its window has the
focus, as with the original.

From the opening, B (Start) goes to the title: the map changes from `0x015d`
to `0x0017`.

Nothing of the game is drawn yet: the game runs with ffx-rust's null graphics
device and silent sound. G1 brings the state API scenes draw from.

## Headless (Linux, tests)

```bash
FFX_GAME_DIR=/path/to/game tools/selftest.sh [--timeout SECONDS]
```

Godot runs headless (no window, no sound), boots the game, waits for the
opening, presses Start through Godot's input and expects the title. It
prints the state every 5 seconds and `ffx selftest: PASS` or `FAIL`; the exit
status is 0 on a pass. The game's documents go to a temporary folder. By hand:

```bash
cd godot
~/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot --headless --path . -- --selftest --documents=/tmp/gd-ffx-docs
```

## Windows

Build the DLL (`tools/build-extension.sh windows`), then open
`godot\project.godot` with the Windows Godot .NET editor
(`%USERPROFILE%\tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe`)
and run it, or headless from a terminal:

```bat
set FFX_GAME_DIR=C:\path\to\the\game
Godot_v4.7.2-stable_mono_win64_console.exe --headless --path godot -- --selftest --documents=%TEMP%\gd-ffx-docs
```

From WSL the game folder is the Windows path of the install (`wslpath -w`).
Running the game counts as playing it: on the development machine, only with
the user's go-ahead (see the repository's `CLAUDE.md`).

## Linux performance note

On Linux the game's large allocations (ffx-rust's arena `VirtualAlloc`) cost
seconds when a map's characters are set up (`Ch_Allocate_InitAll`, about 4 to
6 s on entering the opening). The game catches up afterwards at its own pace.
To be looked at in ffx-rust.
