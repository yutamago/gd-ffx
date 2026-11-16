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
  dropped; nothing goes out until the feeder's target (below) is queued,
  and again after an underrun (Godot's mix asked for more than was
  queued).
  It starts empty when a player attaches and is emptied when the game
  ends (no stale tail, no underruns for the silence after).
- **The feeder** (ffx-rust's, on the game's side) looks every 4 ms and
  mixes 1,600-frame blocks while `audio_queued` is under the target that
  `GodotPlatform::audio_target` gives it: 4,800 frames (100 ms; ffx-rust's
  own default, `FEED_TARGET`, is 6,400, sized for WASAPI's buffer), or
  `FFX_AUDIO_TARGET` (frames, 1,600 to 24,000), while `audio_running` is
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
  stays at the feeder's target and up to a block over: about 5,500
  frames, 114 ms (the headless self-check: `mean 5462`; 147 ms with
  ffx-rust's default target). `FfxGame.state()`
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

**The target.** Measured at Besaid over 60 s headless (developer save
7, `--selftest-besaid --no-walk --selftest-timeout=60`, the game at 30
updates/s):

| Target (frames) | Mean delay | Underruns |
|---|---|---|
| 6,400 (ffx-rust's default) | 147 ms | 0 |
| 4,800 (gd-ffx's) | 114 ms | 0 |
| 4,000 | 97 ms | 78 |
| 3,200 | 68 ms | 699 |

Headless, Godot's dummy driver takes 4,096 frames at once, so it needs a
target above that. A real device takes much smaller pieces, so a lower
target may hold there (not measured: that means playing sound aloud);
`FFX_AUDIO_TARGET` tries one. Below a block (1,600 frames, the mixer's
unit) nothing can go.

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

## Water

`ClassicMaterials`' water shader is `PhyreWaterShader`'s math as its
bytecode has it (both of Besaid's variants): the two normal maps scrolled
by `ScrollSpeed_nml1/2` x time on the two UV sets, their sum recentred as
the normal in the surface's frame, the view ray reflected off it into the
map's cube map; the colour that reflection x `SpecularShininess` x
`SpecularColor` x the vertex colour, then the fog, the colour scale and the
tone curve; alpha (cube alpha x `SpecularShininess` + `MultiTexBlendFactor`
x `MaterialColour.a`) x the vertex alpha; blended over the scene,
double-sided, no depth write. The beach sea's refraction variant mixes the
same colour over the scene behind it by the same alpha (its small
distortion is left out). The material names no cube map (its sampler is
bound outside it): the map's own `reflection*` cube map is used (Besaid has
one, `reflection3`), its six faces from the archive (`FfxGame.classic_texture`,
ffx-phyre's `face_png`); without one, the cube's mean colour stands in. The
D3D cube's z is mirrored into Godot's (`cube_axes`).

The glow pass: the map's glow values (API 9's `Look.glow_*`: Besaid none
below 0.5, full at 1, gain 0.11, spread 0.34) drive Godot's environment glow
(additive, three blurred levels, the threshold at `NoGlowThreshold`;
`--no-glow` turns it off). On the save sphere's orb it brings the patch
closer to the original (29.9 -> 24.0).

The screen is cleared to the map's background colour (`Look.background`,
the `GlobalCamera`'s `m_bgColor`; Besaid 0.306, 0.357, 0.537).

Not matched yet: near the shore the original's sea is a bright, nearly
opaque cyan (the patch at the left of the Besaid picture: 169, 226, 222),
ours paler and more see-through (151, 180, 172). By the formula the colour
before the tone curve is about 0.27 there (0.3835 x the cube's ~0.7 x the
vertex green ~1), which the curve darkens: a factor or the vertex colour
set the water reads is missing. The game's own draw data (the effect
capture's next step) will settle it.

## The map's objects

Scripts move map objects (`graphicObjectSetTranslation`/`Rotation`) and
show or hide their meshes (the layer mask); the file only has where they
start. API 8's `Snapshot.objects` gives each object id's meshes with their
live world matrices and whether they are drawn. `StateView3D` finds the
classic map's mesh nodes by name (`N_kShape`, FBX's `FBXASCnnn` escapes
decoded: mesh k of object N) and places each at its matrix (interpolated
between the last two snapshots), shown while the game draws it. Besaid's
birds (objects 18..32, at the origin in the file) fly where the game has
them; the Besaid check counts the meshes placed (45).

Character models made of rigid parts without a skin (the save sphere's
base `f001`: three meshes `MeshNode0..2`, five `SkeletonNode`s, no skin in
the import) are placed by the game per mesh
(`graphicCharacterInstanceSetWorldMatrixByMdlID`), so posing bones moves
nothing. `StateView3D` moves part `MeshNodeK` with the K-th leaf
`SkeletonNode` (by element) instead: on f001 elements 2, 3 and 4, of which
the game lifts 2 and 3 by 6 and turns them (the ring and its mount) and
leaves 4 at the base. The pairing is inferred from f001, not read from the
game; the picture then matches the original's (the ring floating above the
pedestal). Its glowing orb is an effect, not part of the model.

## The 2D layers (G5)

Many rooms are painted backgrounds: the remaster's 2D layers (ffx-rust's
`docs/formats/scene-maps.md`, "The `YNSC` layer record" and "How the game
draws the 2D layers"). 146 maps have a 2D model,
`map/<area>/<map>/2d/mdl/d3d11/<map>.dae.phyre` (textures in `2d/tex/`):
some only that (`ptkl14`, `mihn03`, `guad01`, `kami01`, `maca01`, `bvyt04`,
`mcfr11`, `nagi07`, `sins00`, the title's `titl00`/`titl02`), some beside a
3D model (`bsil00`).

- **Import.** `ffx-import`'s `import_map_layers` writes it to
  `<cache>/map2d/<map>.glb` (Godot's kind `map2d`); `import_map` takes only
  the 3D model. A level without one of the two answers `none: ...`, which
  is no fault and is not logged (`znkd06`'s 2D model has no meshes and
  counts as none).
- **Meshes.** One quad per layer, `scene<s>layer<l>Shape`, its vertices
  the layer's PS2 screen pixels (x, y on the 512 x 448 screen, y down) at
  its depth z (the `YNSC` record's `+0x14` on most layers).
- **Drawing** (`MapLayers`, under `StateView3D`; the shader in
  `ClassicMaterials`): the layer shader writes clip space itself. x and y
  as the game's orthographic layer camera puts them: the 512 columns across
  the window, `(x - 256) / 256`, and 336 of the 448 rows (16:9),
  `-(y - 224) / 224 x 4/3 + 1/3 - 2b`, b the pan; before that the window's
  shift (scene `+0xfa34`, `+0xfa38`); not squeezed while scene `+0xf9c7`
  or `g_PhyreScene3D` is set. The depth is the main camera's (Godot's
  projection, the game's camera) at view distance 10 z, so the characters,
  drawn as before with the game's camera, go behind nearer layers. The
  colour is the texture x the vertex colour, as it is (no fog, no tone
  curve). The blend comes from the material's effect variant (its switches,
  read from the archive by `FfxGame.ClassicEffectSwitches`; all ten
  `phyreprerenderedshader` variants' D3D11 states checked): every variant
  blends, source alpha over the picture or added (`ADDITIVE_BLENDING`,
  kami01's lamp glows), writes depth unless `MASK_DEPTH`, tests
  `LESS_EQUAL`. The maps' pass, `preTransparentATC`, adds alpha to coverage,
  taken as a cut at alpha 0.5 for the layers that write depth; on maps
  `0xa0`, `0xa1`, `0x101`, `0x16c` the pass is `preTransparent` (no cut).
  The layers are drawn after the opaque world, farther first (render
  priority by depth).
- **What the game decides at run time** is read from its memory each
  frame with `FfxGame.Peek` (scene = `*0x00ccb9d8`, `g_PhyreScene`): the 2D
  scene shown (`+0x11d8`; a map starts on scene 0, scripts change it with
  `PhyreScene_change2DSceneNum`), the layers' shown flags
  (`PhyreScene_show2DLayer`'s table at `+0x1140`, `+0x1138` entries of 8
  bytes: shown, scene, layer id (i16), mesh), the pan b (`+0xf9bc`, f32),
  the shift (`+0xfa34`, `+0xfa38`), `+0xf9c7`, `g_PhyreScene3D`
  (`0x00ccb9c0`), and whether the pass runs (the 2D level loaded,
  `+0x93c`, and `+0x93d` clear; bsil00's one layer, at depth 0.01, is not
  drawn). These belong in the game-state API (ffx-state); until then they
  are a developer's read. The map check (`--selftest-map`) reports
  `2D layers shown/loaded (scene, pan)`.

Measured (`tools/compare-shot.sh`, Xvfb): developer save 52 (kami01) against
the game's screenshot of kami0100 (another moment of the same room) 11.9/255
(before: 101.7, a black screen); the picture lines up with the game's (the
counter, the walls, the floor), the characters on it; the back door is open
because the game hides its closed layer at that moment of the save (the
shown flags). Saves 14
(ptkl14) and 34 (mihn03) show their rooms with the characters behind the
foreground (before: grey planes, black). The sweep's 2D saves (14, 34, 35,
50, 52, 56, 71, 80, 83, 108) pass with no errors.

Not done yet: the layers' texture animation (`textureanimation.ags`, the
`_<frame>` textures), `PhyreScene_changeLayerOffset`/`Scale` (scripts moving
or scaling a layer), the order against the 3D world by `+0xd4` (drawn before
the 3D world when set; here by depth only), and the depth order of the cut-outs
the game gets from `ViewProjection2` (traced only as the main camera's).

## Effects and the battle HUD (G3)

- **The screen distortion** (`DistortionView`): the field's ripple
  (`yiScreff_draw`, script native 0x01e2), from the same capture (FXDR
  version 2): a grid of 81 vertices (positions and UVs in normalised
  screen space, a colour each) drawn over the screen, sampling the picture
  at its UVs times its colour. Besaid runs it (the self-check reports it:
  81 vertices, copy alpha 0.8); there it is nearly the identity (the
  picture against the original: 14.0 with it, 13.8 without). The game's
  smear (its buffer blends 80 % of each new frame over the last) is
  opt-in (`FFX_DISTORTION_SMEAR=1`): it renders the 3D scene a second time
  from the main camera into a viewport of its own and blends that clean
  picture into a buffer kept across frames (the shown frame, which holds
  the grid's own output, fed back into a cyan swirl); 14.0 at Besaid too.

- **The game's own effects** (`EffectDrawView`): ffx-rust's effect-draw
  capture (`ffx_host::vfx`, switched on at boot; `FFX_NO_VFX` leaves it
  off; FXDR version 3) records every draw of the scene's VFX lists with its
  pass: positions in render space, colours, UVs, indices, the texture name
  and the VSP flags. The
  extension hands the frame of the shown snapshot's update to C#
  (`FfxGame.effect_draws`, the `FXDR` encoding), which builds one mesh a
  frame, a surface per (texture, blend): unshaded, two-sided, no depth
  write, the vertex colour doubled (0.5 is the PS2's 0x80). Blend from the
  flags: bit 0x10 subtracts, else bit 0x2 adds, else alpha (from the
  examples 0x18, 0xa/0x2, 0x8/0x9). Textures by the name the game holds
  (`FfxGame.runtime_texture`). At Besaid: 30 draws, 28 textured, 6
  textures; the save sphere's orb (field program 15) and the shore's sea
  strips (which gave the shallow water its cyan) come from here. Passes
  drawn: the main pass, the DTT pass (its `FrameBuffer` draws sample the
  picture so far: Godot's screen texture) and the after-post pass, all in
  the 3D scene. Not drawn: the UI 2D pass (its camera, the scene's second,
  is not captured; only while a scene or movie fades) and the list no pass
  draws. The battle self-check reports the draws seen per pass. The blend
  is the game's own (FXDR version 4: each draw's technique and its shader
  passes' D3D11 blends): `Transparent` writes premultiplied colour for a
  source factor of one, `OptimizedTrans` straight colour for source alpha
  (`EffectDrawView.LookOf`, `Form`). Texture coordinates (FXDR
  version 5): the game uploads a texture's rows as stored, bottom first,
  and its shaders sample the captured UVs unchanged, so on the upright PNG
  a file texture is sampled at `(u, 1 - v)`; each draw's sampler gives its
  address mode (wrap, clamp, mirror). This fixed the save sphere's
  sparkles: read top-down their quad covered four atlas frames (pink
  rings); read as the game does it is one soft glow ball.

- **The draw events** (the same capture): the screen fade (kind 2) is
  drawn by `ScreenFadeView`, a full-screen quad of the event's colour
  (bytes / 128) blended as it says (2 additive, 0x10 subtract, else
  alpha), under the distortion as in the game; the battle's fade-in uses
  it (the battle self-check counts its frames, 36 in the attack). The
  others act on what gd-ffx draws its own way and draw nothing here: copy
  buffer (kind 1), PS2 lighting and specular (3, 4: seen on Valefor's
  overdrive), a character drawn between the effects (5), a depth-only
  pre-pass (6), characters with the event camera (0).

- **Effects as events** (`EffectView`, under the 3D view): each effect
  record the game starts (`Effects.running`, keyed by slot) plays where the
  game plays it, on each target or else on its character, while the game
  keeps the record. A remade effect registered for its id (docs/remake.md)
  is instanced there. Otherwise the game's own draws show it (above); only
  when they are not captured does it get a stand-in, a short burst of
  particles in a colour of its id. Who a character index
  is: in battle the battler (below), out of battle the actor in that slot.
- **The screen shake** (`Effects.shake_x/y`, PS2 screen pixels, 448 high)
  moves the camera's offset by the same share of the view at a subject 100
  units away.
- **The battle HUD** (`BattleHudView`): the party's HP and MP (KO red, a
  quarter or less yellow) by the names the player gave (`Save.members`),
  the turn order by the turn counter (lowest next), and damage numbers: a
  battler's HP change between snapshots, popped over its body (damage
  white, healing green), rising and fading over 1.2 s. Party battler `i`
  is the party actor with id `i + 1`; monster `0x14 + k` the k-th monster
  actor by slot (seen in the deck's battle; `BattleHudView.ActorOf`).

## Native UI (G4)

The game-state API 3 adds the menus' state and the event script's
message windows (ffx-rust's docs/state-api.md, "Menus and messages").
`MessageView` draws the open windows as Godot panels; their text is the
game's own encoding, decoded by `FfxGame.decode_text` with ffx-text's
character set for `World.language`, read from the player's archive
(`ffx-import`'s `Importer::charset`). `BattleMenuView` draws the battle's
command window (the ids named from the kernel tables), the main menu's
options and a save/load list; `FieldMenuView` (API 8's `Save`) draws the
party beside the open main menu: front line then back line by the names
the player gave, HP, MP and sphere levels, the gil and the play time
(`--selftest-menu` opens it with V at Besaid). An open sub-screen shows as
its menu slot's bit in `Menus.main_mask`, the main menu's own (slot 1)
cleared: Items is slot 3 (measured: Items, confirm, the mask goes from
`0x2` to `0x8`). While it is open `FieldMenuView` lists the inventory
(`Save.items`, the game's order) named from the kernel tables, with the
counts; the menu check goes on into it. The PC save and load screens
(`Menus.save_*`) list the files as the game does (`SaveFiles`, from
gd-ffx's documents sandbox: loading, the autosave `ffx_000` then the
manual saves newest first by modification time; saving, a new file's row
then the manual saves), five rows from `save_top` with the cursor, each
with the player's name, the party by the names in that file, the play
time, gil and the place name (`FfxGame.place_name`: `ffx-import`'s
`place_name`, section 11 of the language's `menu/macrodic.dcp`, as
`AtelGetSaveDicName` reads it; entry 1 is "Zanarkand - Stadium");
`--selftest-saves --no-game` checks the header reader on made-up files,
and `--selftest-besaid --check-autosave` on a real one: the autosave the
game writes on entering the map after the beach, read back as the load
list's first row with the game's gil, party and names, at "Besaid Island -
Crossroads" (the developer save does not write one; entering a map does). 
The main menu's screens by their bit (measured: each option picked, a
character chosen where it asks; `FieldMenuView.Screens`): sphere grid
`0x80000` (slot 0x13), items `0x8` (3), abilities `0x4` (2), overdrive
`0x200000` (0x15), status `0x40` (6), config `0x400` (0xa), help `0x800`
(0xb). The status screen shows the menu's current character's HP, MP, the 8
stats, AP, sphere levels and overdrive gauge (API 8's
`menus.current_player`: `g_TkMenuPlayers` at `g_TkMenuCurrentPlayerPos`,
found by diffing the menu's memory with the first and the second
character chosen; the check picks Wakka) (`--selftest-menu
--menu-screen=status`; Tidus and Wakka's start values, which confirm the
stats' order); the abilities screen each member's own learnt commands
(`PartyMember.commands`, `0x3000 + i`, named from the kernel's command
table; the shared ones from `0x3060` are summons and aeons' commands and
stay out), and the overdrive screen each member's mode, the modes obtained
and the gauge (`--menu-screen=abilities|overdrive|status`). API 8's
`menus.windows` (`g_TkMenuWindows` in use) gives the screens' lists and
cursors: on the item screen the list is the window of kind 0 with 56 rows,
its cursor taken as the inventory slot and marked (0 at the start, 1 after
one Down: measured; whether the game's own picture marks that entry or a
row of two is not checked, as gd-ffx does not draw the game's menu). The sphere grid screen
(`SphereGridView`, API 8's `sphere_grid`: the game's own grid while it is
up, read from `*g_Abmap`) draws the 828 nodes and 834 links fitted to the
screen, coloured by kind, the nodes the shown character activated marked
(the node's flags, bit 8 + character: observed), and the character's name
(`--selftest-menu --menu-screen=sphere-grid`), and the game's cursor ringed
with its node's name (`sphere_grid.cursor`, `+0x112fc`; the name from the
kernel's `panel.bin`, `g_AbmapNodeKinds`: `FfxGame.grid_node_name`, kind 0
"Lv. 3 Lock", 2 "Strength +1", 0x60 "Dispel"; at Besaid the cursor starts
on Tidus's Cheer).

## The G0 state read-out

`FfxGame.state()` returns a dictionary read directly from the game's memory
(`rust/ffx-gd/src/state.rs`): the updates and frames run, the game time,
the map, the previous map, the entrance and the story progress
(`g_SaveData`, `0x0112ca90`), and the running event's frame count. The C#
wrapper (`godot/src/FfxGame.cs`) makes it a typed `GameState`; the debug
overlay shows it next to the snapshot.
