# Architecture

The plan is ffx-rust's `docs/godot-plan.md` (decisions by the user,
2026-11-15). In short: ffx-rust stays the game-logic core; Godot draws, plays
the sound, takes the input and hosts the editor.

## The two sides

- **ffx-rust** (the `ffx-host` crate and the ports): runs the game without
  `FFX.exe`'s code where it has a Rust port, at 30 updates a second on the
  real clock, with its own threads (the main loop, the render thread, the
  loader, FMOD, the job workers). Replays, determinism and saves stay its
  business and are checked on its own Windows and headless platforms.
- **gd-ffx** (`rust/ffx-gd`, the GDExtension, and `godot/`, the C# project):
  loads ffx-rust into Godot's process, feeds it Godot's window events and
  input, and reads its state for the scenes.

## The platform seam

Everything the player-facing side of ffx-rust asks of the operating system
goes through one trait, `ffx_host::platform::Platform` (ffx-rust's
`docs/host.md`, "The platform seam"). gd-ffx implements it as
`GodotPlatform` (`rust/ffx-gd/src/platform.rs`):

| Part | In G0 |
|---|---|
| window | windowed: the game's window procedure gets its `WM_CREATE`, then `WM_SIZE`, `WM_ACTIVATE` and `WM_CLOSE` from Godot's window events (queued on Godot's main thread, drained by the game's message pump). `WM_CLOSE` ends the run. |
| graphics | ffx-rust's null device: the game's D3D11 calls are answered and dropped. No original picture is drawn in Godot. |
| input | the system keyboard (DirectInput scan codes, from Godot's physical keys) and mouse (`DIMOUSESTATE2`), Godot's joypads as XInput pads (listed as game controllers with an XInput vendor and product, so the game adds them as XInput devices). Without the focus the keyboard and mouse fail with `DIERR_INPUTLOST`, as the original's foreground devices do. |
| sound | the game's own mix, pulled on Godot's audio thread (G3 step 2, below): `audio_push` appends ffx-rust's blocks to a queue in `Bridge`, `audio_queued` and `audio_underruns` report it, `audio_running` is true once the game started its output and Godot's player is attached. |
| Steam | absent. |
| documents | gd-ffx's own sandbox, passed to the boot. |

The game's threads never call into Godot, and Godot's main thread never
calls into the game's executor. They meet in `Bridge`: a few mutexes and
queues.

## The game's thread

`FfxGame` (`rust/ffx-gd/src/game.rs`) is a Godot node. `start(game_dir,
documents)` installs the platform, then on a thread of its own (64 MB stack)
boots ffx-rust (`ffx_host::boot_opts`: the real clock, the platform's null
device, the start-up logos skipped) and runs the game's entry
(`Exec::run_entry`) until the run ends. `stop()` sends `WM_CLOSE`, then stops
every game thread if the game has not ended. One game per process: ffx-rust's
platform and parts of its state are the process's.

The start-up logos are the remaster's `OpeningScreen.swf`; their end comes
from the Flash movie's `Finish` callback, which a build without Ruffle never
gets. gd-ffx always sets ffx-rust's `Options::skip_opening`, which ends them
on their first frame (Godot shows its own picture).

## Sound (G3 step 2: the pull model)

The mix is ffx-rust's: its FMOD mixer, sample for sample, 48,000 Hz
interleaved stereo f32 in blocks of 1,600 frames (ffx-rust's `docs/host.md`,
"A Godot frontend", "Audio pull"). Godot only plays it.

- **The queue** (`AudioQueue` in `rust/ffx-gd/src/platform.rs`, behind one
  mutex) is kept as ffx-rust's WASAPI output keeps its own
  (`rust_audio_out.rs`): at most half a second (24,000 frames), the oldest
  dropped; nothing goes out until 4,800 frames (100 ms) are queued, and
  again after an underrun (Godot's mix asked for more than was queued).
  It starts empty when a player attaches and is emptied when the game
  ends (no stale tail, no underruns for the silence after).
- **The feeder** (ffx-rust's, on the game's side) looks every 4 ms and
  mixes blocks while `audio_queued` is under 6,400 frames (its
  `FEED_TARGET`: the prefill and one block), while `audio_running` is
  true; otherwise it follows the wall clock and the blocks are dropped.
  The output runs only once Godot's player is attached, so a muted
  frontend (or one without a player) never fills a queue nobody drains.
- **The player** (`FfxAudioOut`, `rust/ffx-gd/src/audio.rs`, a node of the
  extension) is an `AudioStreamPlayer` with an `FfxAudioStream`. Its
  playback, `FfxAudioPlayback`, is an `AudioStreamPlaybackResampled` at
  48,000 Hz: Godot's audio thread calls its `_mix_resampled` for 128
  frames at a time as the device needs them, and it fills them from the
  queue (`AudioQueue::fill`: the queued frames, silence for the rest). It
  calls no Godot API there; Godot's main thread only starts it, before it
  joins the audio server's list. Nothing follows Godot's frame rate, and
  no buffer of ours lies after the queue (the resampler holds one
  128-frame chunk), so `audio_queued` tells the feeder all our buffering.
  The project's mix rate is 48,000 Hz (`audio/driver/mix_rate`), so Godot
  does not resample where the device runs at that rate. The node runs
  while the tree is paused, sets its player up each time it enters the
  tree and lets it go when it leaves.
- **The delay**, mixer to speakers, is the queue plus Godot's output
  latency (its driver's buffer, `audio/driver/output_latency`, 15 ms by
  default, as the driver grants it). The queue's time average while
  playing (`audio_mean_queued`; by Little's law the mean delay of a frame)
  stays at the feeder's target and up to a block over: about 7,000
  frames, 147 ms (the headless self-check: `mean 7060`). `FfxGame.state()`
  has `audio_mean_queued_ms`, `audio_output_latency_ms` and their sum
  `audio_latency_ms`; the overlay's sound line shows them.
- `GameHost` adds the player before it starts the game, unless muted
  (`--mute`, or the project setting `ffx/audio/enabled` false). Headless,
  Godot's audio is its dummy driver: it mixes 4,096 frames every 85 ms at
  the real rate and nothing is heard.

Before (G3 step 1) an `AudioStreamGenerator` topped up to 0.2 s each
Godot frame sat after the queue: about 16,600 frames, 0.35 s, between the
mixer and the driver, and a Godot frame over 0.2 s was a gap (the
generator's skips: 169 in the headless self-check, none now).

**The floor is ffx-rust's.** The feeder's target, 6,400 frames (133 ms),
is a constant in ffx-rust (`crates/ffx-host/src/audio.rs`, `FEED_TARGET`,
used by `feed_due`); `audio_queued` can only make it cover the whole
pipeline, not lower it. A pull on Godot's audio thread needs much less: a
platform hook, e.g. `Platform::audio_target(&self) -> usize` with
`FEED_TARGET` as its default, read by the feeder in place of the constant
(and the frontend's prefill lowered to match), would let gd-ffx keep two
blocks (3,200 frames: 67–100 ms queued, against stalls of the feeder up
to about 55 ms). Proposed, not made: a change on ffx-rust's side.

The game's sound requests (G3's later steps: music and effects as Godot
streams, positional sound) are not routed yet: Godot plays the finished
mix.

## The game-state API (G1)

ffx-rust's `ffx-state` crate defines what Godot reads of the game: a
`Snapshot` after every update with the world (map, story, the running
event, battle, control, pause, fades), the renderer's camera, the pad and
the characters (id, model, root matrix, motion and frame, and every bone's
matrix). ffx-rust's `docs/state-api.md` has the fields, the spaces and the
encoding.

- `ffx_host::observe` reads a snapshot at each frame boundary (the game's
  main thread, between two updates) and hands it to the observer
  `rust/ffx-gd/src/snapshots.rs` registers, which keeps the newest two
  with the time the newest came.
- `FfxGame.snapshots()` gives them to C# encoded; the C# bindings
  (`godot/src/Api/GameStateApi.g.cs`) are generated from the API's schema
  by `rust/ffx-state-cs` (`cargo run -p ffx-state-cs`; a test fails when
  they are stale) and read the bytes through `godot/src/Api/StateReader.cs`.
- `GameStateView` (C#) decodes only when the update changed, and
  interpolates each frame between the two by the time since the newest
  came (`alpha` = age / interval): Godot renders at the display's rate,
  one update behind the game.
- Spaces: the game's field space has y down; its render space negates x
  and y and is Godot's (right-handed, y up, the camera along -z). A field
  point (x, y, z) is (-x, -y, z) in Godot (`GameStateView.FieldPoint`,
  `Scale` 1 until the classic maps settle the units in G2). The camera's
  world comes as the renderer keeps it (`CameraTransform`); a character's
  bones are posed relative to the character and placed by its root
  (`GameStateView.BonePoint`).

The launcher (`scenes/launcher.tscn`, the main scene; docs/launcher.md)
chooses the game folder and the packs, then calls `GameHost.Start` and
loads `scenes/main.tscn`. `ModLoader` (the first autoload) reads the base
remake registry and, from `GameHost.Start`, mounts the mod packs
(docs/remake.md); `GameHost` (an autoload) owns the game (booted in
`Start`), its input and the view;
`StateView3D` draws the camera, the classic map and the characters'
classic models posed by the game (docs/importer.md), markers until they
are imported; `DebugView` is the overlay (F1).

## Native UI (G4)

The game-state API 3 adds the menus' state and the event script's
message windows (ffx-rust's docs/state-api.md, "Menus and messages").
`MessageView` draws the open windows as Godot panels; their text is the
game's own encoding, decoded by `FfxGame.decode_text` with ffx-text's
character set for `World.language`, read from the player's archive
(`ffx-import`'s `Importer::charset`). The menus (field menu, battle
commands, save and load) come next on the same state.

## The G0 state read-out

`FfxGame.state()` returns a dictionary read directly from the game's memory
(`rust/ffx-gd/src/state.rs`): the updates and frames run, the game time,
the map, the previous map, the entrance and the story progress
(`g_SaveData`, `0x0112ca90`), and the running event's frame count. The C#
wrapper (`godot/src/FfxGame.cs`) makes it a typed `GameState`; the debug
overlay shows it next to the snapshot.
