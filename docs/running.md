# Running

## The first start: the launcher

A normal start (`godot --path godot`, F5 in the editor, an exported
program) opens the launcher (docs/launcher.md): choose the game folder
(the one with `FFX.exe`; remembered for the next start), switch mod packs
on or off and order them, see the game's language and the classic cache,
then Start (or Start at a developer save). It is skipped, and the game
starts at once, with `--game-dir`, `FFX_GAME_DIR`, `--devsave`, any
`--selftest...` or `--no-launcher`.

## The game folder

gd-ffx boots the game from your installed copy: the folder with `FFX.exe`
and `data/` (the Steam HD Remaster's install folder). It only reads it. It is
the first found of:

1. `--game-dir=PATH` on the command line, after `--`;
2. the environment variable `FFX_GAME_DIR`;
3. the folder picked in the launcher (`user://settings.cfg`);
4. the project setting `ffx/game/game_dir`.

Started from the launcher, the folder it shows (docs/launcher.md).

The game's settings (`GameSetting.ini`) and saves go to `--documents=PATH`,
else the project setting `ffx/game/documents_dir` (default
`user://documents`, Godot's user data folder for gd-ffx). It is never your
own documents folder, so the original's settings and saves are untouched.

## Starting somewhere

- `--devsave=N` (after `--`): once the game runs, load developer save `N`
  (1..110: the saves the developers left in the archive; game data, not
  your saves). `--devsave=7` is Besaid beach at the save sphere with the
  control. `ffx-import devsaves` lists them all (docs/importer.md).
- `--no-classic`: no classic content (markers and skeletons only; remade
  content still shows).
- `--mods=DIR` (repeatable): mount the `*.pck` mod packs of `DIR` after
  those of `user://mods/`; `--no-mods`: none (docs/remake.md).

## The main scene

`scenes/main.tscn` (loaded once the launcher hands over and `GameHost`, an autoload, booted the game) shows:

- **the 3D view** (`StateView3D`): the game's own camera, the classic map
  of the level the game loaded, and each character as its classic model
  posed by the game's skeleton (docs/importer.md), from the game-state API
  at the display's frame rate (interpolated between the game's updates);
  a marker with skeleton points until a model is imported;
- **the message boxes** (`MessageView`, G4): the game's dialogue, subtitles
  and choices as native Godot panels at the game's places, the text decoded
  from your archive in the game's language (Japanese, Korean and Chinese
  need a system font with those glyphs);
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
the Master bus, about 0.11 s behind the game and the output's own latency
(docs/architecture.md, "Sound"). To turn it off:

- `--mute` on the command line, after `--`;
- or the project setting `ffx/audio/enabled` = false.

Muted, the game still mixes its sound on the wall clock (as ffx-rust does
without a device) and drops it. The debug overlay's `sound` line shows the
seconds played, the frames queued (and their mean), the underruns (Godot's
mix asked for more than the game's mixer had ready: a gap, then 100 ms of
silence while the queue fills again; `FFX_AUDIO_TARGET` sets the frames
kept queued, 4,800 by default: docs/architecture.md, "Sound") and the latency: the mean queue and
Godot's output latency, in milliseconds.

## In a window under WSL (WSLg)

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/run-wsl.sh -- --devsave=7
```

WSLg shows Godot's window on the Windows desktop. Use the OpenGL renderer
(the script passes `--rendering-driver opengl3`): with Vulkan the window
stays white. Without a GPU driver for Mesa in WSL both run on the CPU
(llvmpipe), so the frame rate is modest; the Windows build is the fast way.

## Sweeping the developer saves

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/sweep-devsaves.sh [FIRST LAST] [OUT_DIR]
```

Runs the map check (`--selftest-map`) headless on each developer save
(1..110 by default), one at a time through `tools/capped.sh`: the save's
map or battle map shown with classic content and posed characters for
3 seconds. One line a save: PASS/FAIL with the level, models, objects,
effect draws and distortion, and the log's error and missing-classic
counts (logs in `/tmp/gd-ffx-sweep`). About 25 s a save.

## Benchmark

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/bench.sh [besaid] [besaid-walk] [lchb] [battle] [-- GODOT_ARGS...]
```

Runs Godot headless with `--bench` at developer saves, one run at a time
through `tools/capped.sh`: `besaid` (save 7), `besaid-walk` (save 7, holding
down and left: off the beach to the next map, a load while playing), `lchb`
(save 22, Luca's harbour, a dozen characters), `battle` (save 7 with
`--battle=3,0,0`). Once the scene is shown with its models it waits
`--bench-settle` seconds (3), records `--bench-seconds` (20) of frames and
prints `ffx bench:` lines (`BenchTest`, the probes in `Perf`):

- the frames: fps, mean, p50, p99 and longest frame, frames over 16.7, 33.3
  and 100 ms;
- each view's CPU time a frame (mean, p99, max) and allocations a frame,
  the sum, and the rest of the frame (Godot's own work);
- the main thread's allocations, the collections (by generation) and GC
  pauses, the frames that had one;
- the game: updates a second, snapshots seen and skipped, the gaps between
  them (a gap is a slow update on the game's thread);
- the snapshot and effect frame sizes; Godot's draw calls and objects;
- the longest frames of the load before and of the window, with where
  their time went.

ffx-host's `hitch:` lines (an update over `FFX_HOST_HITCH_MS`, 50 ms here)
come with the game thread's stack. Headless Godot sleeps up to 6.9 ms a frame
when it cannot draw (`OS.LowProcessorUsageModeSleepUsec`: a cap of 145 fps);
the benchmark turns that off, so its frame times are the frame's work.
Headless means the CPU side only: the dummy renderer draws nothing.
`BENCH_RENDER=1` renders offscreen (Xvfb, OpenGL, `--no-vsync`) for the draw
calls; on the development machine that is llvmpipe on the CPU, so its frame
times mean nothing for a real GPU.

The frame rate is not tied to the game's 30 updates a second: Godot draws as
fast as vsync lets it (the project sets no `max_fps`; physics is unused), and
`--no-vsync` unlocks it. The deck preset (`--quality=deck`) caps it at 60.

**G5's measurements** (2026-11-16, WSL2, 16 threads, headless; the C# build
is Debug, as `godot --path` runs it; one run each, the machine shared):

| Scene | | fps | frame mean / p99 / max (ms) | views' CPU a frame | main-thread alloc a frame | longest frame of the load |
|---|---|---|---|---|---|---|
| Besaid | before | 335 | 2.98 / 6.53 / 11.7 | 2.30 ms | 331 KiB | 423 ms (the map 301, the models 110) |
| | after | 1,760 | 0.57 / 3.63 / 12.6 | 0.49 ms | 12 KiB | 58 ms |
| Luca (save 22) | before | 253 | 3.96 / 7.06 / 68.4 | 3.26 ms | 414 KiB | 546 ms (the map 528) |
| | after | 1,397 | 0.72 / 3.76 / 26.7 | 0.59 ms | 13 KiB | 50 ms |
| battle | before | 196 | 5.10 / 7.75 / 10.6 | 1.51 ms (+ 3.6 ms Godot's relayout) | 172 KiB | 408 ms |
| | after | 2,962 | 0.34 / 1.56 / 19.6 | 0.27 ms (+ 0.07) | 6 KiB | 47 ms (the battle map) |
| Besaid walk | before | 682 | 1.48 / 4.39 / **336** (the next map) | 0.93 ms | 171 KiB | 422 ms |
| | after | 4,015 | 0.25 / 1.27 / 29.7 | 0.19 ms | 4 KiB | 43 ms |

Offscreen (OpenGL): 140 to 170 draw calls and 1,050 to 1,170 objects a frame
in these scenes, light for any GPU; the static map meshes are not merged.

What changed (the costs found):

- **Bone posing** (1.8 ms a frame at Besaid): Godot's C# transform math on
  each bone each frame (the matrices decomposed and interpolated, the local
  pose against the parent's global). Each snapshot's bones are now split
  into local poses once (`StateView3D.Locals`, 30 times a second), and a
  frame only interpolates position, rotation and scale (0.3 ms). The
  picture is the same (Besaid against the old code: 0.4 / 255, only what
  moves between the two shots).
- **Allocations** (331 KiB a frame, 38 gen2 collections in 20 s): the
  snapshots were copied out of the extension every frame and both decoded
  each update (now only on a new update, the previous one reused; the
  extension gives `previous_update`); the actors' bone points (cloned
  matrices) every frame (now only for markers); the effect frame decoded
  into new arrays and a new mesh each update, several of them on the large
  object heap (now kept buffers, one `ArrayMesh`, spans passed straight to
  Godot; arrays up to 2 MiB stay in gen0: `System.GC.LOHThreshold` in
  `gd-ffx.csproj`); the overlay's state and text every frame (now four
  times a second).
- **Work redone each frame**: two searches of the whole tree (the map's
  nodes too) by `EffectDrawView` each update, the look and glow pushed to the
  renderer each frame, label colours and texts set again (in battle 3.6 ms
  of Godot's relayout a frame; `Ui.SetText`, `Ui.SetColour`), the sphere
  grid redrawn while closed.
- **Load hitches**: classic maps (0.3 to 0.5 s), characters (10 ms each,
  a dozen at once) and effect textures (35 ms each) loaded on the main
  thread. They load on worker threads now (`ContentResolver`, the glTF and
  its PNGs, the game's materials, the map's object index); the textures the
  loaders need come through `TextureFetcher`: the extension can only be
  called from Godot's main thread, so the asks are queued, the extension
  reads them on its own threads as raw RGBA (no PNG encoded and decoded
  again) and the main thread hands the pixels back. At most three models
  enter the scene a frame.

What remains:

- **Garbage collections**, 7 to 26 ms each while playing. The snapshot
  decoder used to allocate an object and a 16-float array per bone (about
  225 KiB an update at Besaid); it now decodes into a spare snapshot's
  objects (docs/architecture.md, "The game-state API"), and what a snapshot
  costs is the encoded copy from the extension (49 KiB at Besaid, 68 KiB at
  Luca). Measured (2026-11-16, one run each, the machine shared):

  | Scene | | `Snapshots` alloc a frame | an update | main thread a frame | all threads | collections gen0/1/2 in 20 s | paused | frames with one: mean / max |
  |---|---|---|---|---|---|---|---|---|
  | Besaid | before | 4.2 KiB | ~225 KiB | 17.8 KiB | 28.8 MiB/s | 41 / 30 / 10 | 308 ms | 9.4 / 19.2 ms |
  | | after | 1.1 KiB | ~54 KiB | 15.4 KiB | 22.8 MiB/s | 32 / 21 / 11 | 272 ms | 8.8 / 26.0 ms |
  | Luca | before | 5.1 KiB | ~280 KiB | 14.6 KiB | 23.1 MiB/s | 28 / 13 / 2 | 161 ms | 8.1 / 13.0 ms |
  | | after | 1.3 KiB | ~70 KiB | 10.8 KiB | 17.0 MiB/s | 19 / 8 / 2 | 137 ms | 7.6 / 18.6 ms |

  The largest garbage left is the effect frame (`EffectDrawView`,
  `FfxGame.effect_draws`): 344 KiB at Besaid, a new byte array an update,
  on the large object heap (over 85,000 bytes), which is collected with
  gen2: Besaid's 10 or 11 gen2 collections in 20 s. Copying it into a kept
  buffer would need the extension to write into C#'s memory (a pinned
  array's address), as the audio mix does; not done. The views' per-frame
  allocations (`View3D`, `Objects`: 2 to 3 KiB a frame) weigh in the
  benchmark's 1,500 fps, little at 60.
- The first message box: 20 to 27 ms (its glyphs drawn the first time).
- Snapshot frames cost 2 to 4 ms of the views' time (the decoding and the
  bones split), every 33 ms.
- The game thread (ffx-rust; proposals, not for gd-ffx): synchronous
  archive reads on its main thread when a map or event starts
  (`CdReadSync` from `Ch_ReadSync` in `Chr_LoadMotionGroup` and
  `Sg_EventjumpCacheChrs`: 1.1 to 1.2 s; the game shows nothing new
  meanwhile), `Ch_Allocate2` (130 ms, the Linux allocations above), the
  movie start (`PhyFMVPlayerManager_fmvPlayer_play`, 114 ms), the boot's
  `MenuManager_SetUpAllMenuInstance` (170 to 215 ms); in one battle run,
  updates of 50 to 84 ms waiting on its job workers
  (`JobScheduleWait_Ch_CalcMiddle_Batch`), FMOD's command queue and the
  field particles (`pppDrawShapeCameraRender`), while Godot used a core
  at full speed.

## Comparing with the original

`FFX_GAME_DIR=<game> tools/compare-shot.sh REF.png OUT_DIR [devsave]`
renders Godot's picture of a developer save offscreen and compares it with
a screenshot of the original renderer at the same spot (`REF`, your own,
outside git): a mean absolute difference and a side-by-side picture
(reference, Godot, difference x4). Developer save 7 is Besaid beach at
the save sphere.

## Headless (Linux, tests)

`--selftest-battle --devsave=7 --battle=3,0,0` (G3/G4): the battle with its
classic map, the characters posed and the native command menu with the
game's command names and cursor.

`--selftest-message --devsave=6` (G4): the arrival's first subtitle shown
in a native message box, decoded (`--shot=FILE.png` under Xvfb).

`--selftest-besaid --devsave=7` (G2): Besaid's classic map and the
characters' models posed, then a walk down and left off the beach to the
next map (`0x14`); `--no-walk` checks that the map stays without input,
`--shot=FILE.png` takes a picture when Godot renders (not with
`--headless`: under Xvfb, `xvfb-run -a godot --display-driver x11
--rendering-driver opengl3 ...`, nothing shows on screen).

`--selftest-remake --devsave=7` and `--selftest-mods --no-game` (G6):
remade content and mod packs (docs/remake.md); `--selftest-launcher --no-game`:
the launcher (docs/launcher.md).


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
G5). Without the editor: `tools/export.sh windows` builds a standalone
program in `build/windows/` (docs/exporting.md).

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
