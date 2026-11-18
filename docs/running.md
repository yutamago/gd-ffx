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
- `--stand-ins`: with classic content, draw the stand-ins too (a prism,
  skeleton points and the model's name for a character whose model is not
  loaded, particles for an effect without captured draws); without the
  flag such a character is not drawn (docs/architecture.md, "Map
  transitions").
- `--no-hold`: do not hold the picture while a map change loads;
  `--no-prefetch`: do not load the next map ahead (nor keep the last map
  and the characters' models); both for comparing.
- `--mods=DIR` (repeatable): mount the `*.pck` mod packs of `DIR` after
  those of `user://mods/`; `--no-mods`: none (docs/remake.md).
- `--skip-opening`: end the start-up opening on its first frame (the
  self-checks with a developer save do anyway); `--flash-log`: print every
  Flash movie the game opens and every call it makes on one (`ffx flash:
  ...`).

## The main scene

`scenes/main.tscn` (loaded once the launcher hands over and `GameHost`, an autoload, booted the game) shows:

- **the 3D view** (`StateView3D`): the game's own camera, the classic map
  of the level the game loaded, and each character as its classic model
  posed by the game's skeleton (docs/importer.md), from the game-state API
  at the display's frame rate (interpolated between the game's updates),
  each once its model is loaded (a marker with skeleton points only without
  classic content or with `--stand-ins`); a map change holds the last
  picture (or black, with the game's fade) until the next map and its
  characters are ready, and the next map is loaded ahead;
- **the message boxes** (`MessageView`, G4): the game's dialogue, subtitles
  and choices as native Godot panels at the game's places, the text decoded
  from your archive in the game's language (Japanese, Korean and Chinese
  need a system font with those glyphs);
- **the choices and menus** (G5): a choice's options as a list with a lit
  bar and a cursor on the option the game's cursor is on (API 11's live
  `Message.choice`, so it moves with the keys, before the confirm); the
  battle's command rows the same, and while a target is chosen its name
  under the commands and a hand over each selected battler;
- **the boosters** (F1 to F4, the PC version's, kept by the game): a badge
  in the top right for each one on (F1 Turbo x2/x4, dimmed where the game
  does not speed up, e.g. in an event; F2 Supercharge; F3 no or frequent
  encounters; F4 auto battle) and a short toast with each tip the game
  plays, its own text in its language;
- **the game's Flash screens, natively** (G5; docs/architecture.md, "The
  Flash screens"): the start-up opening (the SQUARE ENIX logo and the
  autosave notice, 8.8 s, before the in-game opening; not with a developer
  save or `--skip-opening`), the autosave prompt (the running chocobo at
  the bottom right while the game autosaves), the save and load screen
  (the game's rows, help, count, cursor, its questions; portraits and
  thumbnails from your copy's movie);
- **the pause and the ESC menu** (`PauseView`): Start (B) pauses where the
  game allows it: the picture dimmed, "PAUSE" and the keys. Esc opens the
  game's PC menu, which halts play (the game skips its field update; the
  picture behind it stands still, as the original's); its pages are Flash
  screens gd-ffx does not draw, so gd-ffx shows them natively (the game's
  texts and help lines) and takes the keys and mouse while it is open:
  Resume, System setting (gd-ffx's options, below), Special features (the
  four boosters with their state; one picked returns to the game and
  presses its key), Change parameters (the game's page: get every item,
  every ability or the most gil, after the game's own "irreversible"
  question; enabled where the game's page is), Quit (asks first). Up/Down
  choose, Enter (C, Space) confirms, Esc (Backspace, X) goes back or
  resumes;
- **the options** (F10, or the ESC menu's System setting; "Options" below):
  framed checkboxes that read ticked or not, the control with the keyboard
  or pad focus framed in gold and its caption lit;
- **the FPS overlay** (F9): frames a second, the frame time and the worst of
  the last second, a graph of the last 240 frame times (lines at 16.7 and
  33.3 ms), and the game's updates a second with its speed;
- **the debug overlay** (F12 shows or hides it; it was F1, which is the
  game's now): updates (the game runs 30 a second), game time, the map, the
  story progress, the keys held, and the newest snapshot: the event, the
  pad, the camera and the characters shown with their motion, frame and
  bones.

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
| Esc | the PC menu (gd-ffx's ESC menu over it) |
| F1 | Turbo: normal, x2, x4 (the game's speed-up) |
| F2 | Supercharge on/off (the power-up) |
| F3 | enemy encounters: normal, frequent, none |
| F4 | auto battle on/off |

gd-ffx's own keys never reach the game:

| Keys | gd-ffx |
|---|---|
| F7 | the remaster's compare: the map classic or remastered, in turn, for every map this run (docs/remaster-editor.md, "The render path") |
| F8 | R7a's compare: remade effects, replaced music, sound effects, voice and movies on or off (everything classic) for the run; `--no-replacements` starts with them off (docs/remaster-editor.md, "Effects, audio and movies") |
| F9 | the FPS overlay (saved) |
| F10 | the options (over the ESC menu: gd-ffx presses the game's Esc first, so play halts; alone when the game's menu does not open within 0.7 s) |
| F11 | windowed / fullscreen (saved) |
| F12 | the debug overlay |

While gd-ffx's ESC menu or the options are open the game gets no keys or
mouse, and the keys it held are let go.

## Options

gd-ffx's settings, in game (F10) and in the launcher, applied at once and
saved in the user settings file (`user://settings.cfg`, `--settings=PATH`;
sections `[video]`, `[audio]`, `[debug]` beside the launcher's `[game]`):

| Setting | Choices | Key |
|---|---|---|
| quality preset | High (the project's own settings), Deck (3D at 75 %, no MSAA, 60 fps at most, lighting quality low) | `video/quality` |
| window | windowed, fullscreen (borderless), exclusive fullscreen | `video/window_mode` |
| 3D resolution | 50 % to 200 % of the window (`Viewport.Scaling3DScale`) | `video/scale_3d` |
| upscaler | bilinear; FSR 1.0 and 2.2 under Forward+ | `video/upscaler` |
| MSAA (3D) | off, 2x, 4x, 8x | `video/msaa` |
| screen-space AA | off, FXAA, SMAA | `video/screen_aa` |
| TAA | on/off (Forward+) | `video/taa` |
| vsync | on, off, adaptive, mailbox | `video/vsync` |
| frame rate cap | none, 30, 60, 120, 144, 240 | `video/max_fps` |
| glow | on/off (as `--no-glow`) | `video/glow` |
| sound volume | 0 to 100 % (Godot's Master bus, the game's mix) | `audio/volume` |
| game speed | x0.25 to x8, for debugging (ffx-host's clock: game time and the updates run that much faster; a pink badge shows it while not x1) | `debug/speed` |
| FPS overlay | on/off (F9) | `debug/fps_overlay` |
| render path | as each map says, classic everywhere, remastered everywhere (docs/remaster-editor.md, "The render path"; F7 flips it for the run) | `video/render_path` |
| lighting quality | high, medium, low: what a remastered map's lighting may cost (GI, volumetric fog, SSAO/SSIL/SSR, shadows; docs/remaster-editor.md, "Lighting quality") | `video/lighting` |

The command line wins for its run and is not saved: `--quality=high|deck`,
`--no-vsync`, `--no-glow`, `--speed=X` (0.25 to 8), `--render-path=map|classic|remastered`,
`--lighting=high|medium|low`. The self-checks and the
benchmark start from the defaults and never read the saved file (unless
`--settings=PATH` names one), so a player's settings do not change their
pictures. `--language=CODE` (jp en fr de it es kr ch) writes the game's
`Language=` into the sandbox's `GameSetting.ini` before the boot, as the
launcher does.

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

## Comparing on the GPU (Windows, from WSL)

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/compare-shot-win.sh REF.bmp OUT_DIR [devsave] [vulkan|d3d12]
```

Runs the Windows export (`tools/export.sh windows` first) on the machine's
GPU with Forward+ (Vulkan by default, as the project renders on Windows:
Godot's default; D3D12 lost the device in the remastered view, docs/remaster-editor.md), silently (`--audio-driver Dummy`) and with
`--background-window`: the window takes no focus and is sent behind every
other window (Godot moves an off-screen window back onto the screen, and a
minimised one is not drawn; a covered one still renders). It can show for a
moment as it opens. It scores its picture
against the reference like `tools/compare-shot.sh`. On 2026-11-17 Besaid
scored 47.6 (D3D12) and 47.7 (Vulkan) against the original, where the
Compatibility renderer under Xvfb scored 13.9: the classic shaders compute
in gamma space (as the game does) and Forward+ encoded their output to sRGB
again, so the picture was washed out. With `ClassicPost` (the buffer kept in
the game's values, the game's glow, then the decode: docs/architecture.md,
"Colours") it scores 8.9 on both, and Compatibility 11.8.

## The GPU checks

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD [FFX_REF_BESAID=REF.bmp] tools/gpu-checks.sh [OUT_DIR] [classic lighting materialize character rebuild effects budgets]
```

R8. The standing checks on the machine's GPU in one go: the Windows export
(`tools/capped.sh tools/export.sh windows` first: default features), one check
after another through `tools/compare-shot-win.sh`, silent (Dummy audio,
`--background-window`), Vulkan (Forward+):

| check | what |
|---|---|
| `classic` | Besaid (save 7) classic, scored against `FFX_REF_BESAID` (the player's screenshot of the original there; none: the picture only) |
| `lighting` | `--selftest-lighting`: classic against remastered, R3's colour check |
| `materialize` | `--selftest-materialize`: the GPU material pipeline's parity with the CPU, its times |
| `character` | `--selftest-character`: the stand-in humanoid on Tidus |
| `rebuild` | `--selftest-rebuild --devsave=14`: `ptkl14` rebuilt against its classic layers |
| `effects` | `--selftest-effects`: Besaid's effects by id, the example remake |
| `budgets` | `tools/budget.sh windows besaid` (only when named, or `FFX_GPU_BUDGETS=1`) |

It prints a table (check, result, seconds, the key figure, the GPU) and writes it
to `OUT_DIR/summary.txt` (default `/tmp/gd-ffx-gpu-checks`, a folder per check:
pictures of the game, never in git). It exits 1 when a check fails. Remove the export
(`build/windows`) and OUT_DIR after.

Measured (2026-11-18, the development machine's Intel Arc Pro 140T, Vulkan,
Forward+; 4 min 45 s in all):

| check | result | time | key figure |
|---|---|---|---|
| classic | PASS | 32 s | Besaid against `event-besaid-1.bmp` (the player's screenshot): mean absolute difference 8.9/255, as on main |
| lighting | PASS | 52 s | remastered against classic: dL +0.006, chroma x0.89, hue 1.9° |
| materialize | PASS | 65 s | parity at 1024 px (mean/p99/max of 255): source 0.59/1/1, albedo 0.08/1/1, normal 0.56/2/2, roughness 0.32/2/3 |
| character | PASS | 55 s | the stand-in on Tidus |
| rebuild | PASS | 39 s | `ptkl14` rebuilt against classic 0.00/255 |
| effects | PASS | 41 s | the example remake on `bsil00-fp15` (its recordings into `OUT_DIR/effects-recordings`) |

## Sweeping the developer saves

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/sweep-devsaves.sh [quick|all|N,N,...|FIRST LAST] [OUT_DIR]
```

Runs the map check (`--selftest-map`) headless on developer saves, one at
a time through `tools/capped.sh`. By default (`quick`) on ten saves that
between them cover every kind of map and feature the full sweep has
caught problems in, about 4 minutes:

| Save | Map | Covers |
|---|---|---|
| 7 | Besaid beach | 3D, objects, morphs (birds), effects, distortion, save sphere, cloth |
| 8 | Besaid village | 3D, the exits to the village and its huts |
| 12 | S.S. Liki corridor | a squeezed 2D room |
| 14 | Kilika | 2D layers |
| 22 | Luca harbour | a busy map (13 characters) |
| 29 | Luca stadium | an NPC's cloth |
| 52 | Rin's travel agency | a 2D room with a reference picture |
| 61 | Macalania | mist and pyrefly effects |
| 62 | Bikanel | the desert, the sun's flare |
| 71 | Bevelle | a 2D scene switch |

`all` runs every developer save (1..110, about 50 minutes): for changes
that touch everything (the importer's format, the shaders, the loading
path) and before a release. A list (`7,22,52`) or a range (`1 20`) runs
those. For each save: the save's
map or battle map shown with classic content and posed characters for
3 seconds. One line a save: PASS/FAIL with the level, models, objects,
effect draws and distortion, and the log's error and missing-classic
counts (logs in `/tmp/gd-ffx-sweep`, cleared at the start). About 25 s a
save. `FFX_CLASSIC=DIR`
uses that classic cache instead of the project's.

### Regression pictures

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/sweep-devsaves.sh --pictures [quick|all|N,N,...] [OUT_DIR]
tools/sweep-devsaves.sh accept [OUT_DIR] [N,N,...]     # the last picture run becomes the baseline
```

R8. With `--pictures` (or `FFX_SWEEP_PICTURES=1`) each save runs under Xvfb
(OpenGL, Compatibility on llvmpipe; nothing on screen) and keeps its picture.
The picture is taken at one game update (`--hold-shot`): 150 updates after the map has
shown for 3 s, or the baseline's update (`--hold-update=U`). The last 30
updates before it run at a quarter of the game's speed (ffx-host's clock), so
the views see every update. At U they hold the snapshot itself
(`GameStateView.HoldAtCurrent`: R7b's hold, at the update rather than between
two), so the sea, the effects and the party show the same moment in every run.
Then `devsave-N-map.png` is taken: the same moment without the game's
characters (`StateView3D.HideCharacters`), because the NPCs' wandering differs
between runs even at one update (Kilika's running child). The map picture is
compared with the baseline's (`tools/compare/regress.py`: the mean absolute
difference per channel, 0..255, the share of pixels off by more than 24, the
worst cell of a 4 x 3 grid; `devsave-N-map-diff.png`: baseline, picture,
difference x4). The whole picture's difference is reported beside it. Over
its limit the save's line says REGRESSED and the sweep exits 1.

Some maps do not repeat exactly even at one update: Besaid's foliage sways,
S.S. Liki's corridor sways with the ship, Luca's banner and harbour camera (save
22 starts in a cut scene whose camera is elsewhere at the same update), the
stadium's floor, Macalania's pyreflies and mist. So each save has its noise:
`calibrate` after a run against the baseline with nothing changed keeps that
run's difference per save (the most seen). Calibrate over three clean runs. A
save's limit is 1.5 x its noise + 0.3, at least `FFX_SWEEP_THRESHOLD` (0.5). A
save with noise over 3 is marked loose: it catches only large breaks (a black
or missing map, a broken shader).

```bash
tools/sweep-devsaves.sh calibrate [OUT_DIR] [N,N,...]   # the last clean run's differences as each save's noise
```

The baseline is pictures of the player's game and stays outside git:
`FFX_SWEEP_BASELINE` (default `~/.cache/gd-ffx/sweep-baseline/`), a picture,
its map picture, its update and its noise per save. `accept` copies the last
picture run's (all saves, or the ones listed) into it and clears their noise.
Accept and calibrate again after a change meant to alter the pictures, and
after a Godot or driver upgrade.

Measured (2026-11-18, the quick ten saves, Xvfb, Compatibility on llvmpipe,
3 min 42 s a sweep):

| | |
|---|---|
| two runs held 2 updates apart (Besaid, before the slowed approach) | 1.35/255 |
| held at the same update, characters drawn (Besaid; Kilika's inn) | 0.15; 1.63 (a running child) |
| held at the same update, the map without the characters (two trial runs) | Besaid 0.02, Kilika 0.00 |
| the quick sweep, three clean runs against the baseline (the map pictures), per save | 7 Besaid 0.34, 0.21, 0.24; 8 0.00, 0.00, 0.01; 12 S.S. Liki 0.00, 0.00, 0.80; 14 0.01, 0.00, 0.00; 22 Luca harbour 1.66, 10.21, 16.54; 29 stadium 3.55, 3.44, 4.39; 52 0.00; 61 Macalania 6.11, 4.46, 5.94; 62 Bikanel 1.79, 1.15, 1.58; 71 0.00 |
| the third clean run against the limits of the first two calibrations | 8 of 10 within; 12 (0.80 against 0.5) and 22 (16.54 against 15.62, loose) over; calibrated in since |
| deliberate changes on Besaid (`--overrides-layer`): the editor check's saved overrides (moved, hidden, removed, replaced parts, an added box, the render path remastered) | 24.74/255, 45.7 % of the pixels off: REGRESSED (limit 0.81) |
| the same with R1a's example overrides (small parts) | 1.06/255: REGRESSED (limit 0.81) |

`--overrides-layer=LEVEL=FILE` (any run: `FFX_SWEEP_ARGS="--overrides-layer=/map/bsil/bsil00/=res://remaster/examples/bsil00/overrides.tres"`)
registers an overrides file as the map's last layer for the run, as the checks do.

## Benchmark

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/bench.sh [besaid] [besaid-walk] [lchb] [battle] [-- GODOT_ARGS...]
```

Runs Godot headless with `--bench` at developer saves, one run at a time
through `tools/capped.sh`: `besaid` (save 7), `besaid-walk` (save 7, holding
down and left: off the beach to the next map, a load while playing), `lchb`
(save 22, Luca's harbour, a dozen characters), `battle` (save 7 with
`--battle=3,0,0`), `battle-cloth` (save 12 with `--battle=34,0,0`: Tidus, Yuna
and Lulu, about 1,400 simulated cloth vertices). Once the scene is shown with its models it waits
`--bench-settle` seconds (3), records `--bench-seconds` (20) of frames and
prints `ffx bench:` lines (`BenchTest`, the probes in `Perf`):

- the frames: fps, mean, p50, p99 and longest frame, frames over 16.7, 33.3
  and 100 ms;
- each view's CPU time a frame (mean, p99, max) and allocations a frame,
  the sum, and the rest of the frame (Godot's own work);
- the main thread's allocations, the collections (by generation) and GC
  pauses, the frames that had one; each collection in the window (`gc #N`:
  its pause, the bytes promoted, the finalizable objects found dead, the
  generations' sizes before and after), the build (Debug or Release), the
  .NET version and GC mode, and the collections `GameHost` ran in frames a
  load had already lost (see "Garbage collections" below);
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

`BENCH_PROGRAM=build/linux/gd-ffx.x86_64` runs an exported program
(`tools/capped.sh tools/export.sh linux`) instead of the editor on the
project folder: the C# assembly built ExportRelease and .NET started with
the project's `runtimeconfig.json` (self-contained .NET 9). `godot --path`
runs the Debug assembly on the editor's .NET (10 here, `GodotPlugins.runtimeconfig.json`
rolls forward) with the editor's configuration, so the project's
`System.GC.*` settings do not apply there; environment variables
(`DOTNET_GCgen0size` and the like) do, in both.

`-- --bench-gc-trace` adds the runtime's GC events, read in process by an
`EventListener` (`GcTrace`; no dotnet-trace needed): each collection's
suspension and collection time, what it found alive from each kind of root
(stacks, handles, the older generations through the card table), the objects
it kept for their finalizers, the GC handles; the allocations sampled by
type; and a census of Godot's disposables (read from Godot's
`DisposablesTracker` by reflection) made by each probed section over 100
frames of the window (`--bench-census-all`: every frame after those, all
threads); and for each background load (a map, a character, a map's 2D
layers: `gc load:` lines) the Godot disposables and object wrappers it left
registered when it ended (all finalizable; those a collection during the
load already found dead are not counted). The trace allocates itself: its
run's allocation figures are higher.

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
  Godot); the overlay's state and text every frame (now four times a
  second). `gd-ffx.csproj` sets `System.GC.LOHThreshold` (2 MiB) in the
  project's `runtimeconfig.json`, but `godot --path` starts .NET with the
  editor's own configuration, so it does not apply there (the benchmark's
  `LOH threshold` stays empty): arrays from 85,000 bytes were on the large
  object heap in these measurements.
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

- **Garbage collections.** The snapshot decoder used to allocate an
  object and a 16-float array per bone (about 225 KiB an update at Besaid);
  it now decodes into a spare snapshot's objects (docs/architecture.md,
  "The game-state API"). The effect frame (`FfxGame.effect_draws`: 344 KiB
  at Besaid) and the encoded snapshots (49 KiB at Besaid, 68 KiB at Luca)
  were new arrays an update, the effect frame on the large object heap
  (collected with gen2). Now the extension holds them (`effect_draws_held`,
  `snapshots_held`: the node keeps the bytes until its next call and gives
  their address) and C# copies them into buffers it keeps
  (`Held.CopyInto`, `Marshal.Copy`; main thread only). Also gone: the
  command line read from Godot each frame (`GameSettings.Flag`, `Arg`: two
  string arrays a call, several calls a frame; now read once), a new
  `StringName` for each call into the extension and each shader global set
  a frame (`FfxGame.M`, `ClassicMaterials`, `MapLayers`, the morph
  parameters; a `StringName` is finalizable, so it outlives its first
  collection), `EffectView`'s set and list a frame. With no full
  collection while playing, the Godot objects C# dropped (materials,
  textures) waited for their finalizers until shutdown, after the
  rendering server had stopped (leaked RIDs at exit on 10 developer
  saves): `GameHost._ExitTree` collects and runs the finalizers first.

  Measured (2026-11-16, Debug, headless, the machine shared; two runs
  each, "a / b"; before: `main` at 822ef56; collections and pauses in the
  20 s window):

  | Scene | | main thread a frame | all threads | collections gen0/1/2 | paused | frames with one: mean / max |
  |---|---|---|---|---|---|---|
  | Besaid | before | 12.4 / 11.7 KiB | 20.0 / 20.9 MiB/s | 25/17/9, 27/18/9 | 176 / 172 ms | 7.5 / 14.9, 6.1 / 16.5 ms |
  | | after | 1.8 / 1.8 KiB | 3.6 / 3.6 MiB/s | 5/4/0, 5/4/0 | 87 / 86 ms | 19.5 / 24.7, 19.1 / 24.0 ms |
  | Luca | before | 10.9 / 10.8 KiB | 17.6 / 17.9 MiB/s | 18/7/1, 19/7/1 | 130 / 142 ms | 8.1 / 14.4, 9.0 / 14.2 ms |
  | | after | 2.3 / 2.3 KiB | 4.4 / 4.4 MiB/s | 6/4/0, 6/4/0 | 95 / 80 ms | 16.7 / 23.1, 9.2 / 19.3 ms |
  | battle | before | 7.6 / 7.6 KiB | 27.4 / 27.0 MiB/s | 45/17/2, 44/16/2 | 301 / 303 ms | 7.3 / 13.2, 7.3 / 13.1 ms |
  | | after | 3.6 / 3.6 KiB | 14.0 / 14.1 MiB/s | 22/12/2, 22/12/2 | 194 / 192 ms | 7.8 / 17.1, 9.3 / 15.1 ms |

  With only the effect frame and the snapshots held (one run): Besaid
  5.3 KiB a frame, 8.8 MiB/s, 14/7/0, 169 ms paused, at most 19.3 ms.

  No gen2 collection was left on the field, and the time paused halved at
  Besaid, but each collection paused longer (about 10 ms where it was
  about 3.5), so the worst frame with one was no better (19 to 25 ms); a
  larger young generation (`DOTNET_GCgen0size=0x4000000`, 64 MiB) made each
  one longer still (about 125 ms). What cost was what survived, not the
  garbage: see "Garbage collections" below.
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

### Garbage collections

Measured with the benchmark's collection list and `--bench-gc-trace`
(2026-11-16): at Besaid each young collection (all of them gen1: the young
budget is 12 MiB, the gen1 one smaller) found 40,000 to 73,000 dead
**finalizable** objects and walked 51,000 GC handles. A finalizable object
the collector finds dead is kept alive for its finalizer: traced, copied
into the next generation, queued; that, not the garbage's volume, was the
pause (10 to 28 ms; the time the collector spent marking from each kind of
root, the stacks and the card table included, was small). They were
Godot's C# wrappers that own native memory: a `Variant` of anything but a
plain value (a string, a packed array, a `Basis`, an array, a dictionary,
an object) makes a `Disposer` with a finalizer, `Godot.Collections.Array`,
`Dictionary`, `StringName` and `NodePath` have one, and each registers a
`WeakReference` (itself finalizable, a GC handle) in Godot's
`DisposablesTracker`. A dozen a frame at 1,500 fps fill a young generation
with them. The census (`--bench-gc-trace`) named the makers:

- every frame: the import results and the request replies polled from the
  extension (an `Array<Dictionary>`, a `PackedStringArray`; now asked only
  while an import or a request is pending: `ClassicLibrary.Poll`,
  `GameHost`), `GameState`'s reads (a new string key each,
  four times a second in the benchmark, every frame in the checks);
- every update: the effect mesh (four packed-array variants a surface, and
  the empty blend-shape array and LOD dictionary Godot's
  `AddSurfaceFromArrays` binding makes when they are left out), the light
  `Basis` globals, the snapshot pair's dictionary and its string keys;
- a load: the importers' walks over the scene (`GetChildren`: a new array a
  node), the glTF extras read through Godot's dictionaries (now
  `GltfExtras`, below).

`Variants` (godot/src/Variants.cs) holds the remedies: keys and names made
once (`Variants.Key`, `Variants.Name`), the empty blend shapes and LODs
shared (`Variants.AddSurface`), variants disposed as soon as Godot has
copied them (`Variants.Set`, `SetGlobal`, `CallDictionary`: a disposed
`Disposer` has no finalizer to wait for; its `WeakReference` remains).
Code that calls into Godot every frame should go through it, or call
methods that return plain values.

What a load leaves (its leftovers, finalizable, and what it made and
keeps) would otherwise come out in the first collections while playing:
`GameHost.CollectWhenLost` runs two young collections right after a frame
of 50 ms or more when 4 MiB were allocated since the last collection
(the frame was already a hitch: a map, a battle, characters entering). The
benchmark prints how many (`collected in lost frames`; from the start, the
load included). In the walk to the next map the second one can show in the
frame after the load (4 to 6 ms, after 400 ms).

GC settings: `GameHost` sets `GCSettings.LatencyMode` to
`SustainedLowLatency` (no blocking gen2 while playing, background ones
only; it applies in every build). The project's `runtimeconfig.json`
options (`System.GC.*` in gd-ffx.csproj) apply to exports only: `godot
--path` starts .NET with the editor's configuration (Debug assembly, .NET
10 by roll-forward), where only environment variables
(`DOTNET_gcConcurrent`, `DOTNET_GCgen0size`, ...) reach the runtime. None
was needed: with the survivors gone a young collection costs 0.4 to 2 ms
at the default budget, and a larger budget only spaces them out.

Measured (2026-11-16, headless, the machine otherwise idle; two runs each,
"a / b"; the window's 20 s; before: `main` at e8a7c22 with the same
benchmark; "pause" is each collection's, from `GCMemoryInfo`):

Debug (`godot --path`, .NET 10):

| Scene | | fps | main thread a frame | collections gen0/1/2 | paused | pause mean / max | over 4 ms | finalizable found |
|---|---|---|---|---|---|---|---|---|
| Besaid | before | 1,412 / 1,537 | 1.5 / 1.4 KiB | 4/4/1, 4/4/1 | 68 / 87 ms | 22.0 / 22.9, 28.3 / 44.0 ms | 3 of 3, 3 of 3 | 66,000 |
| | after | 1,548 / 1,520 | 0.6 / 0.6 KiB | 1/0/0, 1/0/0 | 4.7 / 2.8 ms | 4.7, 2.8 ms | 1 of 1, 0 of 1 | 620 |
| Luca | before | 1,512 / 1,576 | 1.8 / 1.9 KiB | 6/6/1, 6/6/1 | 94 / 81 ms | 18.4 / 24.1, 15.9 / 21.4 ms | 5 of 5, 5 of 5 | 48,000 |
| | after | 1,646 / 1,564 | 1.1 / 1.2 KiB | 3/0/0, 3/0/0 | 4.4 / 4.3 ms | 1.5 / 1.6, 1.4 / 1.8 ms | 0, 0 | 1,800 |
| battle | before | 3,285 / 3,530 | 3.1 / 3.1 KiB | 17/17/0, 18/18/0 | 169 / 161 ms | 9.9 / 15.0, 8.9 / 10.8 ms | 17 of 17, 18 of 18 | 29,000 |
| | after | 3,647 / 3,621 | 2.4 / 2.4 KiB | 16/2/1, 16/3/1 | 13 / 10 ms | 0.8 / 2.0, 0.6 / 2.7 ms | 0, 0 | 480 |
| Besaid walk | before | 4,445 / 4,550 | 1.1 / 1.1 KiB | 9/9/1, 9/9/1 | 174 / 173 ms | 21.6 / 24.7, 21.5 / 22.8 ms | 8 of 8, 8 of 8 | 73,000 |
| | after | 4,826 / 4,765 | 0.4 / 0.4 KiB | 3/1/0, 3/1/0 | 7.4 / 7.3 ms | 2.5 / 3.6, 2.4 / 3.3 ms | 0, 0 | 8,900 |

(Debug "after" is d22c80a, before the loader and lost-frame changes that
followed; with them the walk's collection after the load found 7,100.)

Release (the exported program, `BENCH_PROGRAM`, .NET 9 with the project's
`runtimeconfig.json`; before: e8a7c22's code built ExportRelease into the
same export; after: 7ca69ce):

| Scene | | fps | main thread a frame | collections gen0/1/2 | paused | pause mean / max | over 4 ms | finalizable found |
|---|---|---|---|---|---|---|---|---|
| Besaid | before | 7,203 / 7,308 | 1.1 / 1.1 KiB | 13/12/0, 12/12/0 | 333 / 288 ms | 25.6 / 38.5, 24.0 / 33.3 ms | 13 of 13, 12 of 12 | 73,000 |
| | after | 7,629 / 7,436 | 0.3 / 0.3 KiB | 5/0/0, 5/0/0 | 7.5 / 7.9 ms | 1.5 / 1.7, 1.6 / 2.1 ms | 0, 0 | 245 |
| Luca | before | 6,385 / 6,004 | 1.9 / 1.7 KiB | 20/20/0, 17/17/0 | 325 / 341 ms | 16.2 / 26.4, 20.0 / 31.0 ms | 20 of 20, 17 of 17 | 62,000 |
| | after | 8,635 / 7,662 | 0.9 / 0.9 KiB | 13/2/0, 11/2/0 | 11.1 / 10.0 ms | 0.9 / 1.6, 0.9 / 2.8 ms | 0, 0 | 815 |
| battle | before | 13,508 / 13,357 | 3.1 / 3.1 KiB | 68/68/0, 66/66/0 | 695 / 678 ms | 10.2 / 17.4, 10.3 / 26.5 ms | all, all | 32,000 |
| | after | 17,426 / 15,003 | 2.4 / 2.3 KiB | 67/3/0, 57/2/0 | 33 / 25 ms | 0.5 / 1.6, 0.4 / 1.0 ms | 0, 0 | 69 |
| Besaid walk | before | 17,374 / 17,262 | 1.0 / 1.4 KiB | 29/28/0, 28/28/0 | 692 / 685 ms | 23.9 / 27.6, 24.5 / 37.0 ms | all, all | 75,000 |
| | after | 19,218 / 18,998 | 0.3 / 0.3 KiB | 11/3/1, 12/3/0 | 13.3 / 12.8 ms | 1.0 / 2.7, 1.0 / 2.1 ms | 0, 0 | 6,200 |

The frames with a collection: before 10 to 26 ms on average, up to 38 ms;
after 0.6 to 2 ms on average, at most 3 ms (the walk's "frames with one"
include the lost-frame collections in the next map's 360 to 500 ms load).

**Background loads.** The next map's or characters' scenes are prepared on
worker threads while playing. `ClassicMaterials` used to read the glTF JSON
(materials, extras, passes) through `GltfState.Json`, and `StateView3D` and
the transparent sort each mesh's extras through its `extras` metadata: a
Godot dictionary, array or string variant for every value that is not a
plain number, each finalizable. In one of two Release walks three young
collections during such a load found 20,000 to 27,000 and took 8.8 to
21 ms. Now `GltfExtras` reads the .glb's JSON chunk once with
System.Text.Json into plain C# objects (`MaterialExtras`, `PassStates`,
`MeshExtras`); `ClassicLibrary.Load` sets each mesh's fields (object id,
instance, morph and sort fields) on the Godot mesh as integer metadata,
plain variants (`MeshExtras.Of` reads them back; a remade model's mesh, not
loaded there, still by its `extras` dictionary, the variants disposed); the
materials' shader parameters are set by names made once
(`Variants.Name`), resources through variants disposed at once
(`Variants.SetParameter`). The materials are the same: every surface of the
321 cached classic scenes fingerprinted (shader code, render priority,
uniforms, metadata, next pass) with the old and the new reader, identical;
`ClassicMaterials.Made` the same (map 4,503, blend 1,029, characters 892,
layers 298); the 2,916 meshes' new fields equal to Godot's `extras`.

Measured (2026-11-17, headless, `--bench-gc-trace`, two runs each "a / b";
before: fbe1e04, the old reader with the load census; after: cba76b6; the
census's `gc load:` lines, a lower bound; the machine shared with another
agent's sweep in the Debug runs):

| Load | | Godot disposables left | object wrappers |
|---|---|---|---|
| bsil00 (the map, at the start) | before | 7,173 / 7,170 (Variant String 2,392, Dictionary 1,191, StringName 793, Array 758, ...) | 524 |
| | after | 333 to 412 (StringName 219 to 298, made once and kept; Variant String 74) | 524 |
| bsil02 (the walk's next map) | before | 5,995 / 5,995 | 330 |
| | after | 73 / 73 (StringName 70) | 330 |
| c001 (a character) | before | 256 to 333 | 107 |
| | after | 2 to 80 | 107 |
| titl00 2D layers | before / after | 642 / 390 (StringName 385) | 90 / 90 |

The walk's collections (the 20 s window, the next map's load in it):

| Build | | most finalizable objects in one | its pause | longest pause |
|---|---|---|---|---|
| Release (`BENCH_PROGRAM`, .NET 9) | before | 6,923 / 6,362 | 3.7 / 3.0 ms | 3.7 / 3.0 ms |
| | after | 191 / 186 (and 191 / 186 in two more runs) | 1.4 / 1.0 ms | 1.6 / 3.5 ms (2.2 / 1.8) |
| Debug (`godot --path`, .NET 10) | before | 6,458 / 7,167 | 4.3 / 6.2 ms | 15.2 / 5.4 ms |
| | after | 503 / 744 (the start's load) | 3.2 / 4.6 ms | 14.4 / 6.0 ms |

(The Debug runs' longest pauses, with no finalizable objects pending,
came with the other agent's work on the machine.) Frame rates and
allocations a frame did not change beyond the runs' noise; offscreen
pictures of developer saves 7, 10 and 52 differ before/after no more than
two runs of the same build (Besaid's score 13.9 / 255 both).

What is left in a load: a Godot wrapper for each material, mesh and node
the loader touches (the `ShaderMaterial`s made, the scene's walk), the
names each new shader parameter or meta needs once, and the texture
fetcher's answers (main thread, a dictionary a texture).

The battle's HUD and menu allocate 2.4 KiB a frame (no finalizable objects: 0.4 to 1 ms
collections, 3 a second at 3,600 fps, about one every 25 s at 60).

### Cloth

The characters' cloth (docs/architecture.md, "Cloth"), measured with the
same build with the capture off (`FFX_NO_CLOTH=1`) and on (2026-11-17,
Debug, headless, one run each, the machine shared with another agent's
checks):

| Scene | cloth | fps | frame mean / p99 / max (ms) | main thread a frame | collections gen0/1/2 |
|---|---|---|---|---|---|
| Besaid (save 7: Tidus, 104 simulated vertices) | off | 1,493 | 0.67 / 3.49 / 7.1 | 0.8 KiB | 2/0/0 |
| | on | 1,482 | 0.67 / 3.60 / 10.8 | 0.8 KiB | 2/0/0 |
| battle-cloth (save 12, battle 34: 1,433) | off | 2,234 | 0.45 / 2.59 / 10.4 | 3.1 KiB | 12/8/1 |
| | on | 2,378 | 0.42 / 3.02 / 11.7 | 3.1 KiB | 13/5/1 |

The `Cloth` section (the frames fetched, the texture rebuilt once an
update) took 0.008 ms a frame on average in the battle (p99 0.45 ms, at
most 5.4 ms in one frame) and allocates nothing a frame; the frames are
copied into kept buffers, the texture updated in place. The differences
between the runs are their noise.

## Budgets

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/budget.sh [windows|linux] [besaid village kilika luca macalania N...]
```

R8. The benchmark (`--bench`) with its budget record (`--bench-json=FILE`,
`ffx bench: budget {...}`): the map, the preset and lighting quality, the render
path, the GI drawn, the resolution, the renderer and the adapter. It records the
frame ms (mean, p99), the GPU ms of every viewport (`RenderingServer`'s
measured render time; Forward+ only, n/a under Compatibility), draw calls,
objects and primitives a frame, and texture, buffer and video memory. Each map
gets five runs, uncapped (`--bench-uncapped --no-vsync`: the frame time shows the
headroom) over a 10 s window after a 4 s settle:

| run | settings |
|---|---|
| `classic-deck`, `remastered-deck` | the Steam Deck preset: 1280x800, `--quality=deck` (3D at 75 %, no MSAA, the low lighting quality: no GI, no volumetric fog or screen-space effects, shorter shadows) |
| `classic-desktop` | 1920x1080, `--quality=high` |
| `remastered-desktop` | the same, remastered, `--gi=none` |
| `remastered-desktop-gi` | the same with `--gi=voxelgi` (the wizard's GI): the GI's cost is its GPU ms (else frame ms) minus the run without |

`windows` (the default on WSL) runs the Windows export (`tools/capped.sh
tools/export.sh windows` first) on the machine's GPU with Vulkan, behind every
window (`--background-window`, Dummy audio). `linux` runs Godot on the project
under Xvfb: OpenGL (Compatibility) on llvmpipe, or `FFX_BUDGET_DRIVER=vulkan`
(Forward+ on lavapipe). The CPU renders there, so those numbers are marked
llvmpipe or lavapipe and get no verdict. `tools/compare/budget.py` prints the
table and holds each record against its preset's budgets. The table goes to
`/tmp/gd-ffx-budget/budgets.json`, and a copy for the editor's status dashboard
to the project's user folder (`remaster/budgets/<platform>.json`).

The budgets (per map, `tools/compare/budget.py`):

| preset | frame ms | GPU ms | draw calls | texture MiB | video MiB | GI ms |
|---|---|---|---|---|---|---|
| deck (on the Deck) | 16.7 | 16.7 | 2,000 | 1,536 | 3,072 | 0.5 (no GI at low) |
| deck, measured on the Intel Arc 140T | 8.35 | 5.57 | 2,000 | 1,536 | 3,072 | 0.17 |
| desktop (1920x1080, high; the Arc 140T as the low end) | 16.7 | 16.7 | 4,000 | 3,072 | 6,144 | 4.0 |

**The Steam Deck is not here.** Real Deck numbers need the device: run the Linux
export there (`BENCH_PROGRAM=` as for the benchmark, or `tools/budget.sh linux`
with the Deck's own Vulkan driver). Until then the Deck's time budgets are held
against the Arc 140T scaled by an estimate: the GPU's FP32 throughput, about
1.6 TFLOPS for the Deck against about 4.5 for the Arc 140T, a factor of 3; the
CPU about 2. Memory and draw calls are not scaled. A Deck verdict from the Arc is
a warning sign, not a measurement.

Measured (2026-11-18; frame and GPU ms means over 10 s, uncapped; draw calls a
frame; memory at the window's end):

| map | preset | path | GI | frame ms | GPU ms (p99) | draws | texture MiB | video MiB | GI ms | verdict |
|---|---|---|---|---|---|---|---|---|---|---|
| Intel Arc Pro 140T, Vulkan, the Windows export | | | | | | | | | | |
| bsil00 | deck | classic | none | 1.29 | 1.08 (1.42) | 152 | 430 | 473 | | ok |
| bsil00 | deck | remastered | none | 2.11 | 1.92 (2.33) | 333 | 480 | 525 | | ok |
| bsil00 | desktop | classic | none | 3.43 | 3.19 (3.79) | 152 | 492 | 556 | | ok |
| bsil00 | desktop | remastered | none | 5.18 | 4.95 (5.86) | 461 | 569 | 631 | | ok |
| bsil00 | desktop | remastered | VoxelGI | 7.13 | 6.89 (8.12) | 459 | 631 | 693 | 1.94 | ok |
| lchb02 (save 22) | deck | classic | none | 0.91 | 0.71 (1.25) | 162 | 372 | 418 | | ok |
| lchb02 | deck | remastered | none | 1.40 | 1.24 (2.45) | 415 | 517 | 572 | | ok |
| lchb02 | desktop | classic | none | 2.33 | 2.07 (3.46) | 161 | 523 | 614 | | ok |
| lchb02 | desktop | remastered | none | 3.26 | 3.00 (4.58) | 472 | 487 | 579 | | ok |
| lchb02 | desktop | remastered | VoxelGI | 5.57 | 5.30 (7.08) | 473 | 776 | 867 | 2.29 | ok |
| llvmpipe (the CPU), OpenGL Compatibility, Xvfb: no verdict | | | | | | | | | | |
| bsil00 | deck | classic | none | 36.8 | 26.6 | 152 | 546 | 562 | | |
| bsil00 | deck | remastered | none | 55.8 | 44.2 | 333 | 547 | 563 | | |
| bsil00 | desktop | classic | none | 60.7 | 48.1 | 152 | 581 | 597 | | |
| bsil00 | desktop | remastered | none (Compatibility draws no GI: both remastered runs) | 123.5 / 118.3 | 104.4 / 100.3 | 460 | 582 | 598 | | |

On the Arc the Deck preset's remastered Besaid takes 1.9 ms of GPU time, a
third of its 5.6 ms proxy budget, so about 6 ms on the Deck if the factor of 3
holds. The remastered path roughly doubles the draw calls (the lit materials,
the shadow passes). VoxelGI costs about 2 ms at 1080p and 60 to 290 MiB of
textures; at the Deck's low quality there is none.

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
next map (`0x14`), whose map change must be held with no frame of
characters over another map than theirs and no stand-in, and shown within
20 s (its `ffx: transition` line in the result; `--transition-shots=DIR`
under Xvfb: a picture every 50 ms from the map change request);
`--no-walk` checks that the map stays without input,
`--shot=FILE.png` takes a picture when Godot renders (not with
`--headless`: under Xvfb, `xvfb-run -a godot --display-driver x11
--rendering-driver opengl3 ...`, nothing shows on screen).

`--selftest-ux --devsave=7` (G5, the playtest's UX requests): at the save
sphere, confirm opens its two-way choice and the native box's cursor follows
the game's (Down, Up, Down before the confirm; the confirm picks 1); Esc
opens the game's PC menu and the native ESC menu with its five entries,
and play halts under it (the event frame and the motions stand still, the
view's shader clock held),
System setting opens the options, which set the frame cap (applied and
saved), the glow and the game's speed (x2: about 60 updates a second, which
the FPS overlay, F9, shows), with the focus framed and moved by Down; Esc
closes them; Change parameters is enabled, its page shows the game's three
rows, MAX gil's Get brings the game's confirmation, No leaves the gil and
Yes gives 999,999,999 (the game's own callbacks); Esc twice resumes; F3 and
F1 reach the game (the encounter mode and Turbo step, with badge and tip)
and are pressed back; Start shows the pause screen; a treasure's message
keeps its item's name through the text cleaning. The settings go to a
temporary file. `--shot-dir=DIR` under Xvfb writes a picture of each
screen (`parameters.png`, `parameters-confirm.png` and `options-focus.png`
among them); `tools/ux-shots.sh OUT_DIR` takes them all (with the battle's
command menu and target selection) offscreen, as `tools/compare-shot.sh`
renders.

The battle check (`--selftest-battle`) also waits for the target selection
(Attack's first confirm): the native menu names the target and a hand
points at it (`--shot`: `NAME-target.png` too). It collects the game's
damage numbers from the snapshots (API 12), checks each HP number against
the HP its battler lost in that update (all it had when the number is
more: the overkill) and every HP change against a number, and fails when
the HUD popped other numbers than the game's; with `--to-results` also
when no killing blow with overkill popped.

The Besaid check also waits for Tidus's cloth (docs/architecture.md,
"Cloth") and fails when it is not placed on his model, a segment matches
no surface, or it does not move relative to him; the map and battle checks
report it (`cloth: N character(s), ...`). `FFX_NO_CLOTH=1` turns the
capture off (`cloth off`), `FFX_CLOTH_DEBUG=1` tints the cloth vertices
(green near the skinned position, red 2 units off and more). On another
save than 7, `FFX_SHOT_ARGS="--shots=N" tools/compare-shot.sh` takes N
pictures 0.1 s apart (`godot-1.png` ... and `godot.png` last), e.g. with
`--battle=34,0,0` on save 12; the battle check takes them the same way
(`--shots=N` with `--shot`) before its first picture. `--look-at-actor=MODEL`
(e.g. `w012`, Yuna's staff) points the camera at the first shown character
with that model, at its first cloth mesh, close up. `--party=HEX` (with
`--battle`): the battle party written first (`g_SaveInParty`, 8 party
indices, `ff` an empty place; as ffx-rust's `cloth-probe --party`), e.g.
`01000405ff020306` (Yuna, Tidus, Wakka on save 12). `--camera-pitch=DEG`
turns the game's camera up by DEG degrees (the sky, where the game's camera
does not look: Besaid village's sun). The map check's walk logs the
player's field position with each leg and picture (`player at (x, y, z)`)
and the camera's offset, to find a route; developer save 8 (Besaid's lodge
`bsvr02`) reaches the village with `right:3,down:3.5,up:2.6` and the
weavers' hut `bsvr05` with `...,down:1.4,right:5,up:6` (docs/architecture.md,
"The 2D layers").

The Flash screens (G5; docs/architecture.md, "The Flash screens"):
`--selftest-save --devsave=7` opens the save sphere's save screen, checks
the game's rows drawn natively, its question's cursor (on Yes, Right to No,
Left back), saves (a file appears in the sandbox), opens it again with the
saved row, checks the overwrite question starts on No and that No keeps
the file, and cancels; `--selftest-load` (a sandbox with a save, e.g. the
one the save check left: `--documents=` the same folder) plays the
start-up opening, goes to the title, opens Load (its cursor starts on Load
when there are saves), checks "Load this file?" on Yes, Right and No keep
the list, Yes loads the save's map. `--check-flash` (any check; the sweep
passes it) lists the movies the game opened and showed and fails on one
without a native screen or not drawn by it. `--selftest` reports the
start-up opening (logo, notice, the game waited, `Finish`, the loop went
on); the Besaid check's walk reaches an autosave (the prompt played).
`tools/ux-shots.sh` takes pictures of the opening, the autosave prompt and
the save and load screens too.

`--selftest-remake --devsave=7` and `--selftest-mods --no-game` (G6):
remade content and mod packs (docs/remake.md); `--selftest-launcher --no-game`:
the launcher (docs/launcher.md). `--selftest-overrides --devsave=7` (R1): a
map's remaster overrides in the running game (docs/remake.md, "Overrides").


```bash
FFX_GAME_DIR=/path/to/game tools/selftest.sh [--timeout SECONDS]
```

Godot runs headless (no window; its audio is the dummy driver, so the
game's sound is fed and drained but never heard), boots the game, checks that the
game-state API's snapshots come and decode, plays the start-up opening
natively (the game waits for it, as for the original's movie), waits for the party at the fire
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

The default build has no Unicorn and no Ruffle (docs/building.md,
"Features"). Without the editor: `tools/export.sh windows` builds a
standalone program in `build/windows/` (docs/exporting.md).

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
that it comes from the cache. Arrows walk, F12 hides the overlay, F10 opens
the options.

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
