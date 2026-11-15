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

## Starting somewhere

- `--devsave=N` (after `--`): once the game runs, load developer save `N`
  (1..110: the saves the developers left in the archive; game data, not
  your saves). `--devsave=7` is Besaid beach at the save sphere with the
  control. `ffx-import devsaves` lists them all (docs/importer.md).
- `--no-classic`: no classic content (markers and skeletons only).

## The main scene

`scenes/main.tscn` boots the game (`GameHost`, an autoload) and shows:

- **the 3D view** (`StateView3D`): the game's own camera, the classic map
  of the level the game loaded, and each character as its classic model
  posed by the game's skeleton (docs/importer.md), from the game-state API
  at the display's frame rate (interpolated between the game's updates);
  a marker with skeleton points until a model is imported;
- **the debug overlay** (F1 shows or hides it): updates (the game runs 30 a
  second), game time, the map, the story progress, the keys held, and the
  newest snapshot: the event, the pad, the camera and the characters shown
  with their motion, frame and bones.

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

The game's own picture is not drawn: it runs with ffx-rust's null graphics
device; what Godot shows comes from the game-state API.

## Sound

The game's own mix (ffx-rust's FMOD mixer) plays through Godot's audio, on
the Master bus, about a third of a second behind the game. To turn it off:

- `--mute` on the command line, after `--`;
- or the project setting `ffx/audio/enabled` = false.

Muted, the game still mixes its sound on the wall clock (as ffx-rust does
without a device) and drops it. The debug overlay's `sound` line shows the
seconds played, the frames queued, the underruns (the game's mixer fell
behind) and the skips (Godot's mix passes that found the player empty,
each a few milliseconds of silence: after an underrun, or when a frame of
Godot's takes longer than 0.2 s).

## Headless (Linux, tests)

`--selftest-besaid --devsave=7` (G2): Besaid's classic map and the
characters' models posed, then a walk down and left off the beach to the
next map (`0x14`); `--no-walk` checks that the map stays without input,
`--shot=FILE.png` takes a picture when Godot renders (not with
`--headless`: under Xvfb, `xvfb-run -a godot --display-driver x11
--rendering-driver opengl3 ...`, nothing shows on screen).


```bash
FFX_GAME_DIR=/path/to/game tools/selftest.sh [--timeout SECONDS]
```

Godot runs headless (no window; its audio is the dummy driver, so the
game's sound is fed and drained but never heard), boots the game, checks that the
game-state API's snapshots come and decode, waits for the party at the fire
in the opening (at least three party members shown, posed by their
skeletons, and the camera), presses Start through Godot's input and
expects the title, with the game's sound pulled by Godot's player by then
(unless `--mute`). It
prints the state every 5 seconds and `ffx selftest: PASS` or `FAIL`; the exit
status is 0 on a pass. The game's documents go to a temporary folder. By hand:

```bash
cd godot
~/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot --headless --path . -- --selftest --documents=/tmp/gd-ffx-docs
```

## Trying it on Windows

A development build, for your own use (it contains Unicorn, GPL, for the
game functions ffx-rust has not ported yet; a build to share comes with
G5):

1. From WSL: `tools/build-extension.sh windows` (the DLL) and
   `cd godot && dotnet.exe build` (the C# assembly).
2. Open `godot\project.godot` with the Windows Godot .NET editor
   (`%USERPROFILE%\tools\godot\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe`).
3. Project → Project Settings → `ffx/game/game_dir`: your game folder
   (the one with `FFX.exe`), or set `FFX_GAME_DIR`.
4. Play (F5). To start on Besaid beach: Project → Project Settings → Run →
   Main Run Args `-- --devsave=7`, or from a terminal
   `Godot_v4.7.2-stable_mono_win64_console.exe --path godot -- --devsave=7`.

The first visit of a map or character imports it (a second or two); after
that it comes from the cache. Arrows walk, F1 hides the overlay.

Headless from a terminal:

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
