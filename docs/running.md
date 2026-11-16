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

## Comparing on the GPU (Windows, from WSL)

```bash
FFX_GAME_DIR=~/steam-games/FFX-X2-HD tools/compare-shot-win.sh REF.bmp OUT_DIR [devsave] [d3d12|vulkan]
```

Runs the Windows export (`tools/export.sh windows` first) on the machine's
GPU with Forward+, its window placed off the desktop (it renders but is not
seen; a taskbar entry may show while it runs), and scores its picture
against the reference like `tools/compare-shot.sh`. On 2026-11-17 Besaid
scored 47.6 (D3D12) and 47.7 (Vulkan) against the original, where the
Compatibility renderer under Xvfb scores 13.9: the classic shaders compute
in gamma space (as the game does) and Forward+ encodes their output to sRGB
again, so the picture is washed out.

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
