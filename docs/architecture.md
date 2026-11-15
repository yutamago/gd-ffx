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
| sound | the game's own mix, pulled by Godot (G3 step 1, below): `audio_push` appends ffx-rust's blocks to a queue in `Bridge`, `audio_queued` and `audio_underruns` report it, `audio_running` is true once the game started its output and Godot's player is attached. |
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

## Sound (G3 step 1)

The mix is ffx-rust's: its FMOD mixer, sample for sample, 48,000 Hz
interleaved stereo f32 in blocks of 1,600 frames (ffx-rust's `docs/host.md`,
"A Godot frontend", "Audio pull"). Godot only plays it.

- **The queue** (`AudioQueue` in `rust/ffx-gd/src/platform.rs`, behind one
  mutex) is kept as ffx-rust's WASAPI output keeps its own
  (`rust_audio_out.rs`): at most half a second (24,000 frames), the oldest
  dropped; nothing goes out until 4,800 frames (100 ms) are queued, and
  again after an underrun (the queue ran dry while playing). It starts
  empty when a player attaches and is emptied when the game ends (no stale
  tail, no underruns for the silence after).
- **The feeder** (ffx-rust's, on the game's side) mixes a block whenever
  `audio_queued` falls under 6,400 frames, while `audio_running` is true;
  otherwise it follows the wall clock and the blocks are dropped. The
  output runs only once Godot's player is attached, so a muted frontend
  (or one without a player) never fills a queue nobody drains.
- **The player** (`FfxAudioOut`, `rust/ffx-gd/src/audio.rs`, a node of the
  extension) is an `AudioStreamPlayer` with an `AudioStreamGenerator`
  (mix rate 48,000, Godot resamples to the output's rate). Each frame it
  tops the generator's buffer up to 0.2 s (`get_frames_available`,
  `push_buffer`), so a Godot frame up to 0.2 s long leaves no gap; when
  the generator does run empty, its skips (Godot's mix passes that found
  it empty) are counted (`audio_skips`, beside the queue's underruns). The node runs while the tree is paused,
  sets its player up each time it enters the tree and lets it go when it
  leaves. About a third of a second lies between the mixer and the
  speakers (the queue's 6,400 frames and the generator's 9,600).
- `GameHost` adds the player before it starts the game, unless muted
  (`--mute`, or the project setting `ffx/audio/enabled` false). Headless,
  Godot's audio is its dummy driver: the player is fed and drained at the
  real rate and nothing is heard.

**G3 step 2: a pull-model mix.** The refill above follows Godot's frame
rate, and its buffer is delay. Next: a custom `AudioStreamPlayback` whose
mix runs on Godot's audio thread and takes frames from the queue exactly
as the device asks for them (as WASAPI's event does for ffx-rust on
Windows), with no frame-rate dependence and a smaller buffer.

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

`GameHost` (an autoload) owns the game, its input and the view;
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
