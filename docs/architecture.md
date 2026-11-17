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

The start-up logos are the remaster's `openingscreen.swf`; their end comes
from the Flash movie's `Finish` callback, which a build without Ruffle never
gets. Booting to the title, gd-ffx plays the opening natively and calls
`Finish` itself ("The Flash screens" below); with a developer save, a
self-check or `--skip-opening` it sets ffx-rust's `Options::skip_opening`,
which ends the movie on its first frame.

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
- `FfxGame.snapshots_held()` gives them to C# encoded without copies of
  its own: the node holds the encoded pair until its next call and hands
  out each one's address and length, and C# copies the bytes into buffers
  it keeps (`Held.CopyInto`, `Marshal.Copy`) before that next call (all on
  Godot's main thread: gdext calls are main-thread only). No array is made
  an update on either side (`FfxGame.snapshots()`, with Godot arrays, stays
  for the benchmark and tools). The C# bindings
  (`godot/src/Api/GameStateApi.g.cs`) are generated from the API's schema
  by `rust/ffx-state-cs` (`cargo run -p ffx-state-cs`; a test fails when
  they are stale) and read the bytes through `godot/src/Api/StateReader.cs`.
- `GameStateView` (C#) decodes only when the update changed, and
  interpolates each frame between the two by the time since the newest
  came (`alpha` = age / interval): Godot renders at the display's rate,
  one update behind the game.
- Decoding reuses storage: each class's `Read(ref r, into)` fills an
  object read before, its lists (items by index), matrices, byte arrays
  and unchanged strings, so a decode allocates almost nothing.
  `GameStateView` decodes into a spare snapshot, one no longer `Current`
  or `Previous`; the views only keep last frame's two. A reused object can
  come back holding a newer update, so the views' caches compare
  `GameStateView.Decoded` (the decode count), not references (the bone
  poses' per-actor cache drops entries more than one decode old).
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
classic models posed by the game (docs/importer.md), each once its model
is loaded (markers only without classic content: "Map transitions"); `DebugView` is the overlay (F12).

## Map materials

Each map material draws as its effect variant says (docs/importer.md,
"Material states"; ffx-rust's docs/formats/scene.md, "Passes", "Sort").
`ClassicMaterials` takes the pass the maps draw with (`OpaqueNoMSAA`,
`TransparentNoMSAA`, `postTransparent` for the refraction water) and
makes Godot's render modes from its D3D11 states: the cull mode (the game's
`FRONT` with clockwise front faces is Godot's `cull_back` for the exported
winding; `DOUBLE_SIDED` variants have `NONE`), the blend of render target 0
(`SRC_ALPHA, INV_SRC_ALPHA` is `blend_mix`), depth write (`depth_draw_always`
for a blended pass that writes depth, as nearly all blended map parts do;
`depth_draw_never` for `MASK_DEPTH`) and the depth test. One shader is made
per combination of modes and defines. The shader body is the map shader's
math as its bytecode has it (`ffx-import effect --wgsl`):

- texture x `MaterialColour` x vertex colour (all four), the texture at
  the UV set `TEXCOORD0` reads (the import's `ffx_texcoords`: bsil06's
  ground, flat patches of single texels before, keeps its UVs in glTF
  `TEXCOORD_1`), the fog x `fgEff`
  when the pixel program reads `fgEff`, x the colour scale, the tone curve.
  `IGNORE_FOG` variants still fog in their programs; their materials have
  `fgEff` 0 (`lchb00`, `klyt00`);
- blended (`ALPHA_ENABLED`): alpha = texture a x `MaterialColour` a x
  vertex a x `clrScaleFactor` a (the snapshot's colour scale has no alpha:
  1), no alpha test;
- the alpha test only where the pixel program reads `AlphaThreshold` (the
  alpha-to-coverage variants' `OpaqueNoMSAA` twins): texture a <
  `AlphaThreshold` is discarded; other opaque variants ignore alpha;
- the texture's address mode as the material's sampler state has it
  (`ffx_wrap`: clamp or repeat; Godot has one mode for both axes);
- `phyreuvanimationshader` (the scrolling clouds): UV + `ScrollSpeed_X/Y` x
  time, the v scroll negated for the export's v flip (v' = 1 - v), each
  term `fract`ed. The time is the game's `GlobalTime` (API 10's
  `Look.global_time`): the global shader uniform `ffx_time`, set every frame
  by `ClassicMaterials.SetTime`, interpolated between the last two snapshots
  (across a jump, 0 again after a map change, the wrap at 2400 s, a pause or
  a load, the newest value as it is). So the clouds and the water stand
  still while the game does not run, start over on each map, and have the
  game's phase. The map check reports it (`global time`).

Blended parts draw in Godot's transparent pass, which sorts by render
priority, then far to near. The game sorts the map's transparent instances by
`m_render_order` (`ro / 1e8 == 1` first, and instances with `m_DObjKind` 1,
such as sky domes, which the game moves into that class at map init; then
`ro % 1000`, then `(ro % 100000) / 1000`), then far to near, after the
characters' transparent parts. Each distinct order gets a priority from 1
up (characters keep 0), each (material, priority) its own copy of the
material; the refraction water (drawn after the scene) gets 125, its second
pass 126, and the game's effects, drawn after everything, 127 (below).

### Seams between map pieces

The game draws each map piece's float vertex positions with one world matrix
per instance, the file's node world x 10 for nearly all of them, made at
load (ffx-rust's docs/formats/scene.md, "Round 2: open questions", 4), so
the pieces' shared edges meet exactly and nothing shows between them (the
original's own picture, captured with MSAA off, has no cracks). The import
keeps those node worlds (identity on almost every node, under the map's
x 10 root). The cracks came from Godot's glTF loader instead. By default it
compresses the vertex attributes of every surface with normals and no skin
or morph targets, storing positions as 16-bit fractions of that surface's
own bounding box. Two pieces sharing an edge then round its vertices
differently, and single pixels of the clear colour and the sky showed along
the seams (Besaid's sand near the save sphere; both renderers).
`ClassicLibrary.Load` passes `IMPORT_FORCE_DISABLE_MESH_COMPRESSION` (64) to
`GltfDocument.AppendFromFile`. Measured at developer save 7
(Compatibility): 110 of the map's 138 surfaces were compressed, none now.
Isolated bright pixels in the ground half of the picture went from 88 to 9,
and the 9 are on Tidus and the save sphere's glow, where the original's
picture has 9 too. Besaid's score is unchanged (11.6, 11.7 against 11.8).
Under Forward+ (D3D12, `tools/compare-shot-win.sh`) the seams are gone as
well and the score is 8.9 as before; the bright single pixels the count
still finds there lie in the sand's sharper texture at the cliff's foot
(gd-ffx keeps high-quality textures; the reference is low texture quality),
not along an edge.
The cost is the full-size vertex formats (positions 12 bytes instead of 8,
normals and tangents unpacked) on the map's static meshes; characters,
skinned, were never compressed.

## Colours (G5)

The game computes on the stored (gamma) values of its textures and vertex
colours and writes the result as it is into `R8G8B8A8_UNORM` targets: every
blend adds and mixes those values, and the screen shows them unchanged. The
classic shaders (`ClassicMaterials`: maps, water, characters, 2D layers;
`EffectDrawView`; the overlays, which are 2D canvas items) do the same: no
sRGB hint on any texture or colour, the result written as `ALBEDO`
unshaded. The two renderers differ in what they do with it:

- **Compatibility** (OpenGL, `--rendering-driver opengl3`, what
  `tools/compare-shot.sh` runs under Xvfb) writes the values as they are.
- **Forward+** (the export's renderer: D3D12 and Vulkan on Windows, Vulkan
  on Linux) keeps a linear float buffer and encodes it to sRGB in its tone
  map pass: the same values came out brightened and washed out (Besaid
  47.6 / 255 against the original on D3D12, 47.7 on Vulkan, where
  Compatibility scored 13.9). `ClassicPost` (a `CompositorEffect` on the
  `WorldEnvironment`, made only when `RenderingServer.GetCurrentRenderingMethod()`
  is `forward_plus`; `--no-classic-post` leaves it out) fixes that without
  touching the shaders: the buffer holds the game's values all the way
  through the opaque and transparent passes (so blends, the water's
  refraction and the effects' frame copies see what the game's targets
  hold), and after the transparent pass (`POST_TRANSPARENT`) a compute
  shader runs the game's glow on them and decodes the buffer, clamped to
  0..1, from sRGB to linear, so that the tone map's encode (linear, exposure
  1) gives the game's values back. The clear colour is given to Godot
  encoded (`Look.background` through `LinearToSrgb`), as Forward+ decodes it.
  The Mobile renderer is not handled (its buffer is another format, with a
  luminance multiplier).

**The glow** (ffx-rust's docs/formats/postfx.md, "Glow map", "Glow"; the
pixel programs of `phyreglow.fx`, `ffx-import effect --wgsl PASS
PS3Data/Shaders/phyreglow.fx`). `GenerateGlowMap`: per pixel `t c / max(c.r,
c.g, c.b)` with `t = saturate((dot(c, (0.3, 0.5, 0.2)) - min(F, N)) / (F -
N))`, F `FullGlowThreshold`, N `NoGlowThreshold`. Level 0, at half the
picture's size (the game's 640 x 360 at 720 lines): `RenderGaussianBlurX`
and `RenderGaussianBlurY`, 7 taps one texel apart weighted 1/3, 4/21, 2/21,
1/21, each times `GaussianOutputScale` s = spread + 1 (1.34 at Besaid); the
up pass's `RenderGaussianBlurX` (scale 1), then `RenderGaussianBlurYAdditive`
times `GlowGain` added to the picture (one, one). Every target is 8-bit, so
`ClassicPost` clamps each pass's output to 0..1; the level-0 blur reads the
glow map between four of its pixels (their mean), the add reads level 0
bilinearly at the picture's pixels, as the game's samplers do. The game's
levels 1 and 2 are drawn but nothing reads them, so they are left out. One
difference stays: the game draws its effects after the glow, while in Godot
they are part of the transparent pass, so the glow takes them in too.

Under Compatibility (no compositor effects) Godot's environment glow stands
in for it: one level (level 1, half size), the ramp `smoothstep(N, N +
hdr_scale, max(c))` with `hdr_scale` = F - N, intensity gain x s². Before,
it used three levels and `hdr_scale` = 1 / (F - N), which is the ramp's
width, not its slope.

Measured (2026-11-17, developer save 7 against the original's
`event-besaid-1.bmp`, mean absolute difference per channel):

| Renderer | Before | No glow | Now |
|---|---|---|---|
| Forward+, D3D12 (`tools/compare-shot-win.sh`, Intel Arc 140T) | 47.6 | 14.3 | **8.9** |
| Forward+, Vulkan (the same) | 47.7 | | **8.9** |
| Compatibility, Xvfb (`tools/compare-shot.sh`) | 13.9 | 13.3 | **11.8** |

What is left in the Forward+ picture: the shallow water at the shore (see
"Water", "Not matched yet") and the minimap, which gd-ffx does not draw.
Godot's own lit materials (remade content, docs/remake.md) write linear
values into this buffer like the classic ones: they show as under
Compatibility, darker than Godot would show them in a project of its own.

## Water

`ClassicMaterials`' water shader is `PhyreWaterShader`'s math as its
bytecode has it (Besaid's two variants, the textured `kino02`/`lchb02` and
the textured refraction variant of `bika00`): the two normal maps scrolled by
`ScrollSpeed_nml1/2` x `ffx_time` (above), their sum recentred as the normal in the
surface's frame, the view ray reflected off it into the map's cube map. The
colour is ((`TextureSampler` + `TextureSampler1` when `TEXTURE_ENABLED`) +
(0, 0, 0, 1)) x `MultiTexBlendFactor` x `MaterialColour` x the cube map at
the bent normal, plus the reflection x `SpecularShininess` x `SpecularColor`
(alpha: reflection alpha x shininess); x the vertex colour, then the fog,
the colour scale (clamped) and the tone curve; alpha the result's. Each
texture reads its own UV set: the vertex program's `TEXCOORD0`, `4`, `6`,
`7`, which the instance binds to the segment's stream sets (the import's
`ffx_texcoords`), plus its `ScrollSpeed_tex0/tex1/nml1/nml2` x time (v
negated, `fract`ed). Cull, blend and depth write come from the pass (most
water writes depth; 121 parts are `MASK_DEPTH`). `phyrewaveshader` (4
parts) is drawn with the water shader.

The refraction variant (`REFRACTION_ENABLED`, the beach sea; drawn after
the scene, `postTransparent`; bsil00's variant `C1C2...`) writes
mix(colour buffer at an offset, toned colour, alpha). The offset, from its
pixel program: n' = normalize(8 N.x, N.y, 8 N.z) of the bent normal, taken
through `WorldView` (the instance's world, scale 10, included, as the game
does it), (-0.0125 n'x, 0.00625 (-dot(N, V)) n'y) / max(view depth, 10),
clamped to ±0.05, the y part faded by clamp((clip y + 0.9) x 10); the
buffer is read at (u + x, 1 - (v + y)) of the y-up screen uv. Its depth
check (two reads at 2 and 1.2 times the offset, the shift kept only when
their product is above 0.95) reads the `w` of the depth buffer, which for a
D3D11 depth view (R24G8) is 1: it always passes, and gd-ffx leaves it out.

Godot's 3D screen texture S is a copy taken before the transparent pass,
while the game's buffer F holds the transparent draws too (Besaid's
shallow-water layer under the sea): a mix over S wiped them out. So the
water keeps the frame as it is at offset 0 and adds the shift of what is
behind: a x water + (1 - a) x (F + S(offset) - S(here)). The difference is
signed, so it takes two passes (an 8-bit buffer clamps what a pass writes):
the material (premultiplied blend) adds a x water + (1 - a) max(D, 0), its
next pass (subtract, `REFRACTION_SUB`) takes (1 - a) max(-D, 0). The
transparent layers between (the shallow water) are not shifted, which the
game does; at Besaid the shift is a few pixels and the picture scores the
same (sea patch 22.0 without, 21.9 and 24.1 with: the waves' phase moves it
by about that much).

The material names no cube map (its sampler is bound outside it): the
map's own `reflection*` cube map is used (Besaid has one, `reflection3`), its
six faces from the archive (`FfxGame.classic_texture`, ffx-phyre's
`face_png`); without one, the cube's mean colour stands in. The D3D cube's z
is mirrored into Godot's (`cube_axes`).

The glow pass: the map's glow values (API 9's `Look.glow_*`: Besaid none
below 0.5, full at 1, gain 0.11, spread 0.34) drive the game's glow, drawn
as the game draws it under Forward+ ("Colours" below) and approximated by
Godot's environment glow under Compatibility; `--no-glow` turns it off.

The screen is cleared to the map's background colour (`Look.background`,
the `GlobalCamera`'s `m_bgColor`; Besaid 0.306, 0.357, 0.537).

Not matched yet: near the shore the original's sea is a bright, nearly
opaque cyan (the patch at the left of the Besaid picture: 169, 226, 222),
ours paler and more see-through (151, 180, 172). By the formula the colour
before the tone curve is about 0.27 there (0.3835 x the cube's ~0.7 x the
vertex green ~1), which the curve darkens: a factor or the vertex colour
set the water reads is missing. The game's own draw data (the effect
capture's next step) will settle it.

## Characters the game does not draw

A character is shown while the game draws it: API 10's `Actor.drawn`, what
the last `Ch_DrawMain` handed to `graphicCharacterInstanceSetVisible` after
the hide byte, `flags2` bit 0x20, the distance cull, the alpha and its
per-id cases (`GameStateView.Drawn`; the hide byte, the alpha and a matrix
with a size stay as a guard). Before, the hide byte, alpha and size decided
it, which shows a character the game culls or skips while those hold.
`GameStateView.Skipped` collects such characters (model#slot@map) and the
checks print them at the end ("characters the game culled or skipped");
the map check reports `characters drawn N/M`. Measured at Besaid's path
(developer save 11, `bsil01`): at the start 2 of 19 drawn (Tidus and the
save sphere; the others carry the distance cull's hide bit 8), after
walking up 25 s (`--walk=up:25`) 12 of 19; in neither, nor in the walk off
the beach (save 7), is a character culled or skipped while the old test
showed it. A playtester's upside-down character inside a cliff there is
not explained by this yet. `--walk=KEYS:SECONDS` makes the map check walk
before its picture (`FFX_SHOT_ARGS="--walk=up:25" tools/compare-shot.sh`);
legs joined by commas are walked in turn (`--walk=up:4,left+up:3`). The
walk's clock runs only on a shown map 3 s after it came: a map change
releases the keys until the next map has settled, then the walk goes on
with what is left (the log: `walking KEYS for S s (at T s, LEVEL)`).

## Cloth (G5)

A playtester: "physics attachments on characters look frozen in place (long
hair etc)". The snapshot's bones are the elements' matrices only; the game
moves hair and clothes with a vertex simulation of its own (ffx-rust's
docs/state-api.md, "Cloth"). ffx-rust's cloth capture (`ffx_host::cloth`)
records, after each update, every cloth segment the game hands its draw: the
character (actor slot), the segment's PS2 model and piece, and per vertex (in
the segment's vertex order) its render-space position and weight. The game's
character shaders draw vertex `k` at `mix(skinned_k, positions[k],
weights[k])`; so does gd-ffx.

- **The capture** is switched on at boot (`rust/ffx-gd/src/game.rs`;
  `FFX_NO_CLOTH` leaves it off). `FfxGame.cloth_held(update)` encodes the frame
  of exactly that update (`CLTH`; none of another update: its cloth would sit
  where the skeleton is not) into a buffer the node holds until its next
  call, `cloth_address` its address; C# copies it into a kept buffer
  (`FfxGame.ClothInto`, `Held.CopyInto`), as the effect draws.
- **The import** (docs/importer.md, "Cloth") marks each cloth primitive
  (`extras.ffx_cloth`: model, piece, first number, vertex count, the
  `TEXCOORD_n` of the numbers) and numbers its vertices: vertex `k` carries
  `first + k`, which Godot's loader brings in as a `CUSTOMn` channel. The
  mapping does not depend on Godot keeping the vertices' order (it does keep
  it, measured on c001) or not merging equal ones.
- **The load** (`ClothView.Prepare`, from `ClassicLibrary.Load`): each cloth
  surface gets a copy of its character material with the cloth shader
  (`ClassicMaterials.Cloth`: the character shader plus `ClothVertex`, one
  shader per `CUSTOMn`), its mesh the cloth surfaces as integer metadata, its
  node a cull margin of 8 units.
- **Each update** (`ClothView`, from `StateView3D`): when a new frame came (or
  a model with cloth entered), every shown character's cloth meshes are
  placed in one float texture, `ffx_cloth` (RGBA32F, 512 texels a row, grown
  by rows, never shrunk): two texels a vertex number, the previous and the
  current update's position and weight. A mesh's place is its instance's
  `ffx_cloth_base` (an instance uniform; -1 when the frame has none of its
  segments: skinned only). A segment is matched by (actor, instance, model,
  piece, vertex count), the instance being the capture's place in the
  character's instance list and the mesh's `extras.ffx_instance` (the
  import's mesh per instance); a segment of a shown character that matches
  no surface is logged once.
- **Weapons.** A weapon is a character of its own (kind 4, `w...`, bound to
  its holder by `Actor.parent`), with a cloth record of its own: Yuna's
  staff `w012` has two instances, the shaft (0) and the bead strings (1,
  model 1 piece 0, 168 vertices, 156 simulated), and the capture reports
  them as its own actor's instance 1. Weapons are rigid parts without a
  skin (`RigidParts`); their cloth meshes are taken like a skinned
  model's (`ClothView.Of` on every posed model), and the shader's
  "skinned" position is then the part's own (`MODEL_MATRIX`, which the
  game's element places). Of the cached weapons, `w001`, `w011`, `w012`
  and `w101` have cloth. A vertex the previous frame lacks gets the current one twice.
  The frame of update `n` is captured in the render after update `n`; were it
  not there yet when snapshot `n` shows, the previous update's cloth is drawn
  at alpha 1 until it comes (where the bones are at the interval's start).
  Measured: 0 of 128 updates late in the battle below, 11 of 15,475 over the
  110 developer saves' map checks (the checks' notes: `N of M frame(s) after
  their snapshot`).
- **The shader** mixes the skinned position (Godot's `VERTEX` after skinning,
  taken to world space by `MODEL_MATRIX`) with the texel pair's position,
  interpolated by `ffx_cloth_alpha` (the view's alpha, set each frame: the
  bones' interpolation), by its weight, and back to model space. Render space
  is Godot's world, as for the effect draws. Normals stay the skinned ones.
  `FFX_CLOTH_DEBUG=1` tints the cloth vertices by how far the cloth moves them
  from the skinned position (green under 0.25 units, red from 2), by weight.

Measured (2026-11-17, headless; the checks' notes): developer save 7 (Besaid),
Tidus: 5 segments, 198 vertices, 104 simulated, all 104 moving relative to him
each update, at most 0.014 units an update standing still (ffx-rust's probe
found the same); save 29 (Luca's stadium), `c105`: 2 segments, 181 vertices,
117 simulated, up to 0.62 units an update; save 12 in battle 34 (its own party:
Tidus, Yuna, Lulu): 39 segments (1 on a weapon, Yuna's staff `w012`), 2,159
vertices, 1,589 simulated, all moving, no segment without a surface (before
the weapons' cloth: 38, 1,991, 1,433, the staff's beads stiff); with
ffx-rust's probe's party (`--party=01000405ff020306`: Yuna, Tidus, Wakka) 4
characters, 27 segments (1 on a weapon), 1,840 vertices, 1,200 simulated:
ffx-rust's `cloth-probe` numbers. The sweep of the 110 developer saves
(`tools/sweep-devsaves.sh`): all pass, every save shows characters with
cloth (1 to 9 of them), all moving, no segment without a surface. Offscreen (Compatibility, `FFX_CLOTH_DEBUG=1`):
Tidus's hood, jacket hem and shorts green (on the skin, as anchors and short
cloth are); Yuna's long sleeves red (hanging away from the arm's skinned pose)
and changing shape from picture to picture, joined at the shoulder; her skirt
hem, Lulu's dress hem and belts green. Without the tint nothing is torn or
detached. The cost: 0.008 ms a frame on average in that battle (p99 0.45 ms;
the texture rebuilt 30 times a second), nothing allocated a frame (the bench's
`Cloth` section; docs/running.md, "Benchmark").

Offscreen, the staff close up (`--look-at-actor=w012 --shots=6`, save 12,
battle 34): its two bead strings hang from the collar under its head and
swing from picture to picture, joined to it; tinted, the right string green
(near the stored pose), the left one red (hanging 2 units and more away from
it).

Under Forward+ (Vulkan on Mesa's lavapipe, offscreen under Xvfb:
`VK_ICD_FILENAMES=.../lvp_icd.json`, `--rendering-driver vulkan`) the tinted
picture of Tidus is the same as under Compatibility.

Not yet: the cloth's normals (lit as skinned); D3D12 and a real GPU not run.

## Map transitions (G5)

A playtester saw the characters of the next map over the old one before the
new map came up, and coloured prisms, bone spheres and floating names (and
particle bursts) until models loaded. Both came from drawing each part as
soon as it was there: the snapshot has the new map's characters at once
(their places, the new camera), while its map took 0.3 to 4 s to load.

- **The hold** (`StateView3D.UpdateTransition`, `TransitionView`). A map
  change starts when the snapshot's level is not the map shown (the game
  started a new map), while the game holds its update for a load
  (`World.scene_loading`), or once its map change request
  (`World.map_change`) has the screen black (a fade to or from black at
  1). It ends when the map is shown (and on a field map its 2D layers), the
  game loads nothing, and every character the game draws has its model; or
  after 10 s (an import not seen before). Meanwhile `TransitionView`, in the
  overlay above the screen fade and filters and below the message boxes and
  menus, covers the 3D view with the last frame drawn (the viewport's
  picture, taken once), black from the moment the game's fade is black (and
  before any map was shown). So the new map and its characters come up
  together, with the game's fade-in where it is by then. The map change
  request alone does not hold while the screen is not black, so the game's
  fade-out plays. `--no-hold` turns the hold off (the old behaviour, for
  comparing).
- **No stand-ins with classic content.** A character whose model is not
  loaded is not drawn; the stand-ins (the prism, the skeleton points, the
  model's name; `EffectView`'s particle burst for an effect without
  captured draws) are made only without classic content or with
  `--stand-ins` (`StateView3D.StandIns`). A character appearing later on
  the same map is not drawn until its model is loaded (no hold).
- **Kept models and maps.** A character's model goes to a pool (16 models)
  when its character leaves, for the next one with that model: the party
  is the same characters re-created on each map. The map left last is kept
  out of the tree with its object index, for the way back.
- **Loading ahead** (`MapPrefetch`). Where the player goes: while
  `World.map_change` is set, `World.map` is its target; else the current
  map's exits, the maps its event script jumps to with constant arguments.
  `ffx-import`'s `exits` reads them: the event of each map number from
  `event/header/eventid.bin` (as the game's `initEventList` reads it:
  `(offset, _)` pairs, the first offset over 8 the count, the names at the
  offsets; map `0x46` is `bsil0000`, `0x14` `bsil0200`), the event's `.ebp`
  decoded with ffx-rust's `ffx-script`, and each call of `0x0011`
  `Native_MapChange`, `0x00ab` `Native_00ab_MapChange` or `0x010c`
  `Native_010c_MapChangeDefault` whose two arguments (map, entrance) are
  pushed just before it as constants (`pushimm`, `pushc`). 402 map numbers
  have an event, with 1,307 such calls; the beach has one (to `0x14`),
  `bsil0200` four. The level is the event name's map (`bsil0200`:
  `/map/bsil/bsil02/`); a wrong guess only loads a map that is not used.
  The extension reads them on a thread (`FfxGame.map_exits`, `ready` false
  until done). Each candidate's map and 2D layers are imported one at a
  time (the extension's import threads), and the first candidate's scene
  (and the map change's target) is built on a worker
  (`ContentResolver.Start`, the same key the view's `Resolve` takes it by);
  at most one is held, the older dropped. Under the headless dummy
  renderer, whose loads run on the main thread, only the imports.
  `--no-prefetch` turns loading ahead, the pool and the kept map off.
- **The next battle's map** (`MapPrefetch.TickBattle`,
  `rust/ffx-gd/src/encounters.rs`). The game says which battle maps a
  field map's random encounters use: its encounter table (`btl.bin`, held
  from the start: the fields at `*0x0112a9c8`, 14 bytes each, their count
  the `u16` at `0x0112a9d0`, the blocks at `*0x0112a9cc`; ffx-rust's
  docs/formats/battle.md), whose field for the map a random encounter
  looks up (`g_SaveData + 4`, not the event map at `+0`) lists groups of
  a battle map word (`0x400` a battle map, low ten bits its number) and an
  encounter rate. A map is files `2 n` and `2 n + 1` of CD directory 0x1a
  (battle) or 0x13 (field); their names come from the game's copy of
  `cdrom.fid` (`g_CdLib + 0x2c`, the directory's first file number) and
  `cdrom.fnd` (`g_CdLib + 0x30`), e.g.
  `host0:/ffx/master/jppc/btlmap/mihn/mihn00_a/bin/mapout.vpa`, the level
  `/btlmap/mihn/mihn00_a/`. Where an encounter can come (no battle, not
  switched off with F3), the levels of the groups with a rate are
  imported and the first one's scene held beside the exits' (one more,
  `KeepBattle`). Besaid's beach (`+4` map 0x41) has no field: none; the
  Mi'ihen highroad's start (developer save 33, `mihn00`, `+4` 0xd2):
  `mihn00_a` and `mihn00_b`, rate 50 each.
- **A map shown at once is still a change.** A level whose map was loaded
  ahead (or kept) is shown in the snapshot's first frame of it, so nothing
  was pending and the hold never began: its characters came up one by one
  as their models loaded. A new map level now starts the hold too, which
  ends once the characters' models are there.
- **The log.** Each change prints `ffx: transition FROM -> TO: shown N ms
  after the game's new level (its map, where it came from: loaded ahead,
  kept, loaded then; the 2D layers, the game's load, the characters' models),
  M ms after its map change request, held H ms`.

Measured (2026-11-17, the Besaid check's walk off the beach to `0x14`,
developer save 7, offscreen under Xvfb (OpenGL on llvmpipe, loads on
workers), the machine shared with another agent's jobs; "after the new
level": the snapshot's level `bsil02` to the picture shown; "after the
request": from `World.map_change` set):

| Cache | | after the new level | after the request | peak memory |
|---|---|---|---|---|
| warm (imported before) | before (`--no-prefetch --no-hold`) | 1,213 ms, 15 to 18 frames of Tidus over the beach | 2,103 ms | 2.44 GB |
| | hold, no loading ahead (`--no-prefetch`) | 1,048 to 2,807 ms, held | 2,075 to 4,056 ms | 2.25 to 2.30 GB |
| | now | **0 ms** (loaded ahead) | 775 to 1,009 ms | 2.70 GB |
| cold (a new cache) | hold, no loading ahead | 3,917 to 3,997 ms | 4,750 to 5,031 ms | 2.32 GB |
| | now | **0 ms** (imported and loaded on the beach) | 1,023 to 1,043 ms | 2.53 GB |

A battle start (Xvfb, developer save 33, `--selftest-battle --battle=18,0,0`:
Mi'ihen's field, group 0, `mihn00_a`): loaded then (`--no-prefetch`),
held 725 to 730 ms (its map 523 to 612 ms); loaded ahead, held 64 ms (its
map 0 ms). Peak memory of that run 2.05 GB, against 1.68 GB with
`--no-prefetch` (which also turns the exits' loading ahead off).

What remains after the request is the game's own: its fade out, the black
it holds and its load (`TkInitScene`), before its new level. Headless, the
maps load inline on the main thread, so the change shows in the frame of the
new level either way (the check's 1,006 ms held are that black). The
pictures (`--transition-shots`): before, the new map's camera and Tidus at
his new place over the beach for 15 frames; now the beach, then black (the
game's fade), then the next map with Tidus in its fade-in.

## The map's objects

Scripts move map objects (`graphicObjectSetTranslation`/`Rotation`) and
show or hide their meshes (the layer mask); the file only has where they
start. API 8's `Snapshot.objects` gives each object id's meshes with their
live world matrices and whether they are drawn. `StateView3D` finds the
classic map's mesh nodes by the object id the import gives each mesh
(`extras.ffx_obj_id`, the instance's `m_objID`, by which
`PhyreScene_rebuildMapObjects` lists an object's meshes; mesh k of object
N is the k-th of those meshes in instance order, `ffx_instance`), and by
name (`N_kShape`, FBX's `FBXASCnnn` escapes decoded) for an import without
the ids. The names are not always the ids: at Besaid the mesh named
`6_0Shape` is object 5's. Each is placed at its matrix (interpolated
between the last two snapshots), shown while the game draws it. Besaid's
birds (objects 18..32, at the origin in the file) fly where the game has
them; the Besaid check counts the meshes placed.

Some map meshes morph (docs/importer.md, "Morphing meshes"): the import
gives a mesh its instance's `m_objID`, `m_animCt`, `m_mimeCt` and
`m_dObjFlag` and a position morph its morph target. `StateView3D` finds the
meshes the game's morph manager registers (`MorphManager_registerClusterMorphs`:
`m_animCt` above 0; `m_mimeCt` 1 with a target, 4 or 3 with a material of
the weight, else `m_dObjFlag` `0x400` with a material of the UV offset)
and sets each from its object's display-object value (`SceneObject.morph`,
`morph_v`, interpolated; 0 for an object that is not a display object), as
`MorphManager_setWeights` does by `m_objID`: the blend shape (weights `1 -
w`, `w` are base plus `w` times the target), or the map shader's instance
uniforms `ffx_morph` (`UV_MORPH`, `VERTEX_COLOR_MORPH`) and
`ffx_uv_offset` (`UV_SCROLL`; ClassicMaterials). Besaid's birds flap their
wings (the morph value runs 0..1), its shore strips (objects 0..5, 13) and
the bsil06 ground (8 meshes) move. The checks print the morphing meshes;
`--no-morph` keeps every mesh in its stored pose, `--look-at-object=N`
points the camera at object N (Besaid's birds fly far above the save 7
view: `FFX_SHOT_ARGS="--shots=4 --look-at-object=19" tools/compare-shot.sh`).

Character models made of rigid parts without a skin (the save sphere's
base `f001`: three meshes `MeshNode0..2`, five `SkeletonNode`s, no skin in
the import) are placed by the game per model, as `Ch_DrawMain`'s first walk
does: each model not in the skin list at the character's world times its
base element's matrix (`graphicCharacterInstanceSetWorldMatrixByMdlID`,
which finds a model's meshes by their segments' model id, `PMeshSegment
+0x18`). The import writes that id (`extras.ffx_model_id`, docs/importer.md)
and API 10 gives each model's base element (`Actor.models`), so
`StateView3D` puts mesh `id` at `bones[models[id].base]` (f001: models 0, 1,
2 on elements 2, 3, 4; the game lifts 2 and 3 by 6 and turns them, the ring
and its mount, and leaves 4 at the base). Its glowing orb is an effect, not
part of the model.

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
  drawn with the game's camera and its screen adjustment ("The camera's
  projection" below), go behind nearer layers. The
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
- **What the game decides at run time** comes from the game-state API's
  `Snapshot.layers` (API 10; scene = `*g_PhyreScene`): the 2D scene shown
  (`scene`, `+0x11d8`; a map starts on scene 0, scripts change it with
  `PhyreScene_change2DSceneNum`), the layers' shown flags (`entries`,
  `PhyreScene_show2DLayer`'s table: shown, scene, layer id; a layer not
  listed is shown, the last entry for a layer wins), the pan b (`pan`), the
  shift (`shift_x`, `shift_y`), whether the window is squeezed
  (`squeezed`: not while `+0xf9c7` or `g_PhyreScene3D` is set), and whether
  the pass runs (`drawn`: the 2D level loaded and the pass not switched off;
  bsil00's one layer, at depth 0.01, is not drawn). Before API 10 these were
  read from the game's memory with `FfxGame.Peek`. Since API 11 the window's
  pan, shift and squeeze are taken from `Camera` (the same scene fields),
  interpolated between updates with the camera (`GameStateView.Camera`), so
  the layers and the characters move together; `Layers` is the fallback
  without a camera. The map check
  (`--selftest-map`) reports `2D layers shown/loaded (scene, pan)` and the
  camera's projection.

Measured (`tools/compare-shot.sh`, Xvfb): developer save 52 (kami01) against
the game's screenshot of kami0100 (another moment of the same room) 11.9/255,
11.2 with the camera's projection (before: 101.7, a black screen); the picture lines up with the game's (the
counter, the walls, the floor), the characters on it; the back door is open
because the game hides its closed layer at that moment of the save (the
shown flags). Saves 14
(ptkl14) and 34 (mihn03) show their rooms with the characters behind the
foreground (before: grey planes, black). Besaid village's huts (`bsvr04`,
`05`, `06`, `09`, reached from the village `bsvr00`; a playtester saw the
background slide against the characters in one): developer save 8 with
`--walk=right:3,down:3.5,up:2.6,down:1.4,right:5,up:6,down:2.5,left:2,up:2
--shots=24` walks into the weavers' hut `bsvr05` (11 layers, squeezed) and
back and forth in it; the window pans, the camera's offset from -0.257 to
0.263, and the two weavers at the loom move with the painted room: from
offset -0.092 to 0.263 the loom and the bench move 128 pixels up, the
weavers 127 and 128 (0.355 x 360 = 127.8), Tidus's feet on the floor in
every picture. The sweep's 2D saves (14, 34, 35,
50, 52, 56, 71, 80, 83, 108) pass with no errors.

Not done yet: the layers' texture animation (`textureanimation.ags`, the
`_<frame>` textures), `PhyreScene_changeLayerOffset`/`Scale` (scripts moving
or scaling a layer), the order against the 3D world by `+0xd4` (`Layers.before_world`, now in
the snapshot but not used: here by depth only), and the depth order of the cut-outs
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
  (`FfxGame.effect_draws_held` and `effect_draws_address`, the `FXDR`
  encoding held by the node until its next call, copied into a buffer C#
  keeps, as the snapshots are), which builds one mesh a
  frame, a part (child `MeshInstance3D`) per run of draws with the same
  texture and blend: unshaded, two-sided, no depth write, the vertex colour
  doubled (0.5 is the PS2's 0x80). The game draws them after the whole
  scene, so they take render priority 127, above every blended map part,
  and keep the game's order among themselves by their sorting offset
  (Godot sorts the transparent pass by priority, then far to near, and
  does not keep the order of equal keys). With priority 0 the map's blended
  parts were drawn over the save sphere's orb wherever they lay behind it:
  it looked cut flat and behind the ring; it is round and inside the ring
  now. The subtract family (0x18) writes premultiplied colour in both
  techniques, which removed a dark wedge on the shallow water (Besaid's
  sea patch 30.9 -> 22.0, the picture 14.2 -> 13.9). Blend from the
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

  Each draw's shader is the one its VSP flags pick (`EffectDrawView.Shader`,
  effects-draw.md "Shaders"): the flags as `vfxShader_fx.txt` lists them
  (every line's `VSP_*` bits; 62 shaders), else, as
  `ClassDynamicGeometry_SetShader` retries, the shader of bit 8 alone
  (`VfxDefaultShader` with a texture, `VfxNoTextureShader` without). The
  lens flares (`KeLnsArnd/Crn/Fls_Draw`: the frame's mode `| 0x800`, `|
  0x40` for flag bit 3) are such flags: `0x80a` is `VfxNZAdditiveShader`,
  `0x84a` is not listed and draws with the default shader. What the shader
  writes (`Form`) follows from that shader's flags: textured ones
  premultiplied under `Transparent` (straight under `OptimizedTrans`; the
  textured subtract family under both), untextured additive ones `(2 v.a
  v.rgb, v.a)`, the other untextured ones `(2 v.a v.rgb, 2 v.a)`. Before,
  any flags but the decoded families' fell to the straight form, and with
  a `(one, one)` blend and no texture that was a flat quad of the vertex
  colour at full strength: the white rectangle of the sun's glare. Seen
  over the 110 developer saves (`FFX_EFFECT_LOG=1` in the sweep): lens
  flares `0x80a` in saves 13, 30, 62, 65, 88, 97, 98 (none is in Besaid
  village's outdoor maps), `0x808` (`VfxNoZTestShader`) in 61, and `0x40`,
  `0x240`, `0x249`. Under `Transparent` these are premultiplied; the old
  straight form ignored the alpha that holds the HD glow textures'
  brightness (their colour is white): in Luca (save 30) the sun's glare was
  a white wedge over a third of the picture (61.5 / 255 against the new
  picture), in Macalania (save 61) the no-depth-test mist covered the
  whole screen in white (139.6 / 255); now the glare is faint and the woods
  misty with the pyreflies as small lights. Besaid village (where a
  playtester saw "the sun and sun glare is a white rectangle" over the
  temple square; no developer save starts outdoors there: from save 8,
  `--walk=right:3,down:3.5,up:2.6,up+right:3.4` leaves the lodge `bsvr02`
  for `bsvr00` and stops before the temple) draws the same flares, `0x80a`
  with `map/bsvr/bsvr00/fp/tex` textures; offscreen the square shows the
  sun as a soft glow above the temple and, the camera turned up 30 degrees
  (`--camera-pitch=30`), as a bright core with rays in a halo, under
  Compatibility and under Forward+ (Vulkan on lavapipe), no rectangle.

  **Unset vertex colours.** Some captured draws index vertices whose colour
  is no colour the game writes (`FFX_EFFECT_LOG=1` prints the first of each
  kind, `odd colour`): the allocator's fill `0xcdcdcdcd` (-4.3e8 as a
  float) or stale numbers such as (2303, -241, 0, 2623). At Besaid village
  the lens flare's ring quad (`bsvr00`, key lo `0xd`, texture
  `13888_19_0_0_128_64`, 16 vertices) holds `0xcdcdcdcd` on every vertex,
  in the sky by the sun; the village's other effects (keys lo 5, 7, 0x271b)
  and a battle effect texture (`15456_...`) stale numbers. Under the
  premultiplied form (`a min(t c, 1)`, `a = t.a c.a`, both factors hugely
  negative) such a triangle is white wherever the texture's alpha is not
  0. `EffectDrawView` leaves out triangles with a vertex colour outside
  0..8 or not finite (`EffectDrawView.Unset`; `--keep-unset-colours`
  draws them, for comparing); the map check counts the draws
  (`N with unset vertex colours so far`). With them or without, the
  pictures above are the same (the flare's rings are faint), so this is a
  guard rather than the cause of what the playtester saw; where the values
  come from (an array slot the capture reads that the game did not fill,
  or the game's own) is for ffx-rust's capture. Over the 110 developer
  saves' map checks (2026-11-17) 94 saves show such draws, nearly all
  `0xcdcdcdcd` on every vertex (save 29, Luca's stadium: one kind of
  battle-effect particle quad, 10,300 draws in the check, drawn straight,
  where the colour clamps to nothing anyway); save 7's picture with and
  without them differs only where things move.

  An
  untextured shader samples nothing, whatever name the segment holds; the
  shader's `VSP_ZTEST_DISABLE` (0x800) draws without the depth test,
  `VSP_ZWRITE_ENABLE` (0x40) writes depth. A textured shader's texture
  that is not found is read three times, a second apart, then logged once
  (`ffx effects: texture "..." not found in the archive`) and its draws are
  not drawn (before: drawn untextured, a flat quad); the map check prints
  them (`N without their texture`). `FFX_EFFECT_LOG=1` prints each kind of
  draw once (flags, shader, technique, pass, key, texture, look, first
  vertex colour). Entries the game hides (`graphicVFXSetVisible(.., 0)`,
  `PhyreScene_setMeshVisible` on the geometry's mesh) are not on the
  capture's list: the list is rebuilt each frame by the geometries'
  updates (`VfxGeometry_Register`), `KeLnsFls_Draw` hides instead of
  updating, and neither `DrawMostVFX` nor the capture tests visibility.

- **The draw events** (the same capture): the screen fade (kind 2) is
  drawn by `ScreenFadeView`, a full-screen quad of the event's colour
  (bytes / 128) blended as it says (2 additive, 0x10 subtract, else
  alpha), under the distortion as in the game; the battle's fade-in uses
  it (the battle self-check counts its frames, 36 in the attack). The
  others act on what gd-ffx draws its own way and draw nothing here: copy
  buffer (kind 1), PS2 lighting and specular (3, 4: seen on Valefor's
  overdrive), a character drawn between the effects (5), a depth-only
  pre-pass (6), characters with the event camera (0).

- **The screen filters** (`ScreenFilterView`, `Effects.filters`, the
  game's `g_FadeSlots`): one full-screen quad a slot, in slot order, with
  the alpha `Sg_DrawFilter` (`0x0082c0c0`, ffx-rust's
  `ffx_sg::control::draw_filter`) hands to `graphicAddScreenFadeTask`, its
  integer quotients as the game takes them. The snapshot is read after the
  update, so after the draw's own step of the frame counter n: mode 2 (from
  black) `1 - (127 - n 127 / total) / 127`; mode 3 (to black) `1 - (n 127 /
  total) / 127`, and at 0 the game writes 1 back and draws 1.0, so a held
  fade (the snapshot's 1) is fully black; mode 4 (from white) `((n + 1) 255
  / total) / 127`, up to 2, clamped to 1; mode 5 (to white) `(255 - n 255 /
  total) / 127`, 255 held; mode 6 (a flash) `((n + 1) 127 / total) / 127`;
  modes 7 and 8 (the colour filters) the level / 127, 8 subtracted (the
  task's flag 0x10). Modes 2 and 3 draw black, the others the slot's colour
  (bytes / 256). Before, a held fade was `1 - 1 / total` (0.967: not black),
  4 and 5 were drawn white and the filters at half their alpha. The Besaid
  check watches them on its walk off the beach: the map change fades to
  black in mode 3 and holds at 1.000 (118 frames); a fade to black that
  stays below 1 fails it.

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
  the turn order by the turn counter (lowest next), and the game's own
  damage numbers (API 12's `Effects.numbers`, ffx-rust's docs/state-api.md,
  "Damage numbers"): the game's list of number records joined back into
  numbers (battler, value, colour, word, age, life). `MsSubHP` registers the
  HP loss it is handed before it clamps the HP at 0, so a killing blow pops
  its overkill (the deck's battle: 268 on a monster, with the Overkill word,
  and 108 on one with 100 HP). Each number pops once, the first time a
  snapshot lists it (its key: the update it was put in, `update - age`, with
  battler, colour, word and value, so a skipped snapshot loses nothing), over
  the battler's body, and follows the game's timing: its characters show
  lowest digit first, one an update (place `k` from age `k + 1`); a loss
  bounces by the game's own frame table (`g_BtlNumberFrames`, read from the
  running game with `FfxGame.Peek`, not kept in gd-ffx), a heal (green)
  rises by half its age, the Overkill word by its age, in PS2 pixels; the
  last 5 updates of its life (30) fade, and it leaves when the game drops
  it. The words: `0x3a` MP and `0x3d` Overkill from their callers; `0x3b`,
  `0x3c` are taken to be Miss and Immune by the PS2 font's order (not seen
  yet). Before API 12 the HUD popped each battler's HP change between
  snapshots, and a monster that left the list in the update of its blow
  popped its last HP: never the overkill. Party battler `i`
  is the party actor with id `i + 1`; monster `0x14 + k` the k-th monster
  actor by slot (seen in the deck's battle; `BattleHudView.ActorOf`).

## The window: 16:9 with bars (G5)

The game fits its picture to 16:9 with bars in a window of any other shape
(`PostProcessManager_setNon16By9Viewport`, ffx-rust's render.md). gd-ffx
does it with Godot's stretch mode (`project.godot`: `canvas_items`, aspect
`keep`, base 1280 x 720): the root viewport is the largest 16:9 rectangle
in the window, at the window's own pixels (2560 x 1080 draws the picture at
1920 x 1080, bars left and right), so the 3D camera, the 2D layers (whose
clip space spans the viewport) and the overlays always see 16:9 and agree.
The 2D canvas is scaled from 1280 x 720 to that rectangle, so every panel
and font size in the views is in 720-line units and grows with the window:
the message boxes' text is 24 (`MessageView.FontSize`), 36 px at 1080
lines, the game's glyph height. The game is told the window's own size
(`DisplayServer.WindowGetSize`, its `WM_SIZE`; the viewport's visible rect
is the 1280 x 720 base), sent again whenever it changes.

## The camera's projection (G5)

The game's main view-projection (`PhyreScene_getViewProjectionMatrix`;
ffx-rust's docs/state-api.md, API 11, "The camera's adjustment", and
render.md) is the camera's perspective (vertical field of view `fov_y`, its
own aspect `a`) times the screen shift, then, when squeezed, the 2D window's
adjustment (`PhyreScene_GetScreenAdjustedView`). In clip space:
`x' = x + shift_x w`, `y' = (y + shift_y w) 4/3 + offset_y w` with
`offset_y = 1/3 - 2 (map_pan + pan)` (squeezed; else `y + shift_y w`). On
3D maps `a` is 16/9 and nothing is squeezed. In the 2D-background rooms
(`g_PhyreScene3D` clear) the camera keeps the PS2's 4/3 on the 16:9 target:
the PS2 picture is stretched by 4/3 across the window, and the squeeze picks
336 of its 448 rows, panned with the player (`pan`). That is the 2D layers'
window ("The 2D layers"), so the characters must get the same squeeze or
they slide over the picture while it pans: before, Godot's camera had only
`fov_y` with the viewport's 16:9, so in the S.S. Liki's corridor (save 12)
and kami01 (save 52) the characters were smaller than the room and stood at
the wrong height on it.

`GameProjection.Apply` sets it on the Godot camera. The adjustment is an
off-centre frustum, which Godot's `Frustum` projection can express (it has no
custom matrix): at the near plane n, `2 n tan(fov_y / 2) / k` high (k = 4/3
squeezed, else 1), offset by minus the clip offsets times half its width
and height. Godot takes the width from the viewport's aspect, 16:9 (the
window's bars: "The window"), which is the game's horizontal scale exactly
when `a = 16/9 / k`: the 3D maps and the 2D rooms. A camera with no offset
and no squeeze keeps Godot's plain `Perspective` (Besaid's picture is the
same as before: 11.6 against the original, 11.8 before). Another pairing
keeps the height and the offsets, takes the view's width, and is logged
(`ffx: camera aspect ...`) and counted (the map check's `mismatched`) while
a map is shown. Before one (the opening's movie, the loads) the scene is
16/9 and squeezed, with nothing 3D drawn; that is not counted. The sweep
(2026-11-17) has no other pairing: 19 saves are 2D rooms (4/3, squeezed),
91 are 3D (16/9, not squeezed; Besaid's village, `bsvr02`, saves 8 and 9,
is one of them), 0 mismatched frames on a map.
`GameStateView.Camera` interpolates the shift, the pan and the offset
between updates as it does the camera's transform (the ship's sway,
`shift_y`, moves a few thousandths an update). `DistortionView`'s clean
camera copies the whole projection (`GameProjection.Copy`).

Measured (Xvfb, `tools/compare-shot.sh`, `--walk=up:3 --shots=4`: pictures
during the walk): save 12 (`slik04`, 4/3, squeezed, pan 0.295, offset
-0.262 on the map check's last frame): before, the sailors at the far end
of the corridor were off the picture and the near one stood in the middle
of the floor, small; now the far ones stand on the painted floor by the
doors and the near one's feet are on the carpet, at the room's scale,
through the walk. Save 35 (`mihn03`, `--walk=down:3`): the window scrolls
as the player walks (pan 0.260 to 0.139) and the characters go with it
(the seated man stays on his stool, the shopkeepers behind the counter).
Save 52 (`kami01`, pan 0.275, offset -0.217): 11.2 against
the game's screenshot (11.9 before), the shopkeeper behind the counter and
Tidus on the mat as in the game's.

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
(`--selftest-menu` opens it with V at Besaid; its line also names the
joined party's weapons as the results screen names gear, below). The
names are the saved bytes decoded in the running language, as the game
draws them. A developer save holds them in the Western encoding ("Tidus",
"Wakka"), so with the game in Japanese both the game and gd-ffx show
`ぅずさてつ` and `えげぜぜげ`: `TkDebugLoadViewerSave` copies the save over
`g_SaveData` and never runs `setDefaultPlayerName` (which a normal load,
`TkMenuCopyLoadedSave`, does: a name marked default, `+0x13`, becomes the
language's default name, and between Western and Korean/Chinese saves it is
converted; ffx-rust's `ffx_battle::save`). The developer saves' `+0x12` and
`+0x13` bytes are not a language and a flag (save 7: 120 and 124 for
Tidus); `ffx-import devnames <n>` lists them. An open sub-screen shows as
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
and the gauge (`--menu-screen=abilities|overdrive|status`). The config screen (API 10's
`menus.config`, while the mask has `0x400`) lists its rows: each row's
title and options decoded from the game's text, the chosen option in
brackets, the game's cursor marked, disabled rows dimmed. The PC row
(`pc_row`, row 0) has localisation ids only (title `0x1cd`, option `k`
`0x1d0 - 2 k`): as the game does (`SaveDataGetLoc`,
`LocalizationManager_getText`), `FfxGame.loc_text` takes line `id` of the
remaster's localisation kit, `FFX_Data/GameData/PS3Data/LocKit/FFX_LOC_KIT_PS3_<LANG>.BIN`
in the archive (`ffx-import`'s `loc_text`: lines end at `\n`, `\r`
dropped, past the end the last line; the game's encoding), decoded in the
running language: "Music" with "Arranged", "Original" in English,
"ＢＧＭ設定" with "アレンジ", "オリジナル" in Japanese (without the archive
"PC settings" and "Option 1", "Option 2"). `ffx-import loc <lang> <id>...`
prints lines. `--menu-screen=config` goes down
the main menu to option id 8 (`TkMenuMainOpenOption` opens slot 0xa), as
ffx-rust's test does, confirms, and checks that one Down moves the marked
row (measured at Besaid: 8 rows; subtitles, their names, the cursor, help,
summons, the guide map, vibration). API 8's
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

## Cursors, boosters, the ESC menu and the options (G5, playtest round 2)

- **Choices.** API 11's `Message.choice` is the option the game's cursor is
  on while the choice is open (before, the confirmed pick, so the cursor
  seemed to move only on Circle); `picked` is the confirmed one.
  `MessageView.Layout` splits a window's text into the lines above the
  options and the last `flags >> 8` lines, drawn as a `MenuList`: a lit
  bar and a cursor drawn as a shape (`CursorMark`), not the `▶` glyph that
  some fonts lack.
- **Battle targets.** While the command window's level chooses a target
  (`Menus.battle_target.open`, its record's kind 1; that level has no
  command ids), `BattleMenuView` names the target (the battler the cursor
  is on, its index in its side's list, the side) and `TargetHands` draws a
  hand over each selected battler (`mask`, as `TOBtlDrawTargetCursor`), at
  its body through the game's camera; none while the auto-battle runs. The
  game's own hand cursors (`hands`) belong to its command list's place on
  the PS2 screen, which the native menu does not share: not drawn.
- **Boosters.** F1 to F4 go to the game (the debug overlay moved to F12).
  `BoosterView` follows `Snapshot.boosters` for its badges and shows the
  game's tips as the game plays them (`OnPlay` on `box.swf`, "The Flash
  screens" below; before, it guessed them from the boosters' changes):
  the localisation kit's lines that the game hands its Flash pages and
  `TipSystem` are UTF-8, not the game's encoding
  (`FfxGame.loc_text_utf8`; `loc_text` decodes the config's PC row, which
  is in the game's encoding): 0x2fd..0x2ff the speed, 0x2f6..0x2f8 the
  power-up, 0x2f9..0x2fb the encounters, 0x37d..0x380 the auto-battle.
- **The ESC menu.** `Menus.esc_menu` (`g_FFEscMenu`) is up while the game's
  `escmenu.swf` page is; meanwhile `animate` skips `updateFFX` (ffx-rust's
  docs/formats/frame-timing.md): play halts, as in the original (measured
  under the menu: the event frame and the characters' motion frames stand
  still). The snapshots keep coming (a frame boundary each frame) and three
  wall-clock counters of the game's run on, as they do in the original:
  game time (the Phyre timer; the original measures the time the menu was
  open, `g_FFEscMenuClosedTime`, for `Sg_FrameSync`), the shaders'
  `GlobalTime` and the play time (`Atel_PlayTimeUpdate_0086ca20` adds the
  `graphicGetTime` difference, so the original counts the menu's time too).
  The original's picture stands still (`afterGraphicSync` stops swapping
  once `graphicIsEscMenuActive`), so the 3D view holds its shader clock
  under the menu (`StateView3D`: no scrolling clouds or water behind it);
  halting ffx-host's clock instead would stop the frame limiter, and with
  it `FFEscMenu_update`, which closes the menu. gd-ffx draws its pages
  natively and takes the input (`GameHost.Capture`), so the Flash page's
  own cursor never moves. The home page: localisation lines 0x217 Resume,
  0x219 System setting, 0x21b Special features, 0x21d Change parameters,
  0x21f Quit (`FFEscMenu_initializeMainSettingPage`'s `setHomePage`), with
  the game's help lines for its own entries (the help table `0x00c33dc0`:
  `(text, page, index)`, page 1 rows 0x218..0x220, page 13 below); Resume
  taps the game's Esc (`GameHost.Tap`: held 0.1 s, as the game reads its
  keyboard once an update), which closes the menu from any page. A
  booster picked on the Special features page is tapped once the menu has
  closed (the game clears the booster bits while paused).
- **Change parameters** (the game's page 13,
  `FFEscMenu_initializeParameterChangePage`: three `addChangeParaPageItem`
  rows of kind `spinnerButton`): "Get all items" 0x2b6, "Unlock all
  skills" 0x2bd, "MAX gil" 0x2c4, each with its "Get" button 0x2c8 and
  help 0x2b7, 0x2be, 0x2c5 (0x2c7 "Get max status" is in the table, row 3,
  but the game adds no such row). The Flash page talks to the game only
  through AS3 callbacks; without Ruffle the movie is a null player and
  calls none, so the extension calls them in its place, through the
  game's own dispatcher (`ffx_host::iggy::as3_call`, which matches the
  menu's player `[[menu + 0x6c]]`), on the game's main thread
  (`rust/ffx-gd/src/esc.rs`): `FfxGame.start` wraps `FFEscMenu_update`
  (`0x0068ffb0`, once a frame while the menu exists; prologue `55 8b ec
  83 ec 18`) in the function table before the boot (`Table::wrap`, the
  table's modding seam: no ffx-rust change), and the wrapper runs the
  queued calls before the original, whose `IggyMenuHandlers_flush` runs
  what they queue. A row's button is `EscMenuButtonClick([13, row])`:
  `FFEscMenu_onParameterButton_0068f610` keeps the row in
  `g_EscParameterButtonArg` and queues
  `FFEscMenu_onParameterButtonClicked`, which, when no movie fades
  (`g_MovieFading`), the scenario flag's low 16 bits are set and the map
  is not an Eternal Calm scene, shows its confirmation (0x2c9 "This
  operation is irreversible.@el@Do you want to use this function?", 0x2ca
  Yes, 0x2cb No) and sets its answer `FFEscMenu_onDebugFill_006fc970` at
  `g_FFEscMenu + 0x10c`; else the movie's `showPopUpStd("Function not
  Available now!")` (English in every language, read from the game). The
  wrapper reports after each update whether that answer waits (`+0x10c`),
  the row, and, while the menu is open, the page's enable condition
  (`setEnableChangePara`, computed with the game's `getScenerioFlag` and
  `isEternalCalmScene`): `FfxGame.esc_state`. `PauseView` greys the entry
  where the game's page would, draws the rows with the button at the right
  (`MenuList`: a row's text after a tab), and on the game's confirmation
  draws its message over Yes and No, the cursor on No (nothing undoes it;
  which button the movie starts on was not looked at); the answer is
  `EscMenuPopUpConfirmed(1)` or `(0)`
  (`FFEscMenu_onPopUpButton_0068f8f0` calls the answer with it and clears
  it). Yes runs `MsDebugFullItem` (every common item to 99),
  achievements 0x19 and 0x18 then `MsDebugFullCommand` (every ability), or
  `MsDebugFullGill` (999,999,999 gil). Measured at Besaid (developer save
  7): the entry enabled, MAX gil's Get brought the confirmation (row 2),
  No left 665 gil, Yes gave 999,999,999.
- **The options.** `UserSettings` (saved, applied live: docs/running.md,
  "Options"); the glow switch is the run-time flag `no-glow`
  (`GameSettings.SetFlag`), which the 3D view already reads. The game's
  speed is ffx-host's clock (`Clock::set_speed`, ffx-rust's docs/host.md
  "Game speed"): `FfxGame.set_speed` before the boot goes into
  `Options::speed`, afterwards to the running clock; measured at Besaid,
  x2 gives 59.9 updates a second headless.
- **The settings' look** (`GameTheme` in `Ui.cs`, on `OptionsPanel`: the
  overlay and the launcher). Godot's default checkbox was a dark outline
  on the dark window, unreadable unticked. Now every box is a light frame
  on a near-black fill, ticked ones filled in the cursor's gold with a
  dark tick (disabled ones dimmed); buttons and lists take the window's
  blue, lit when hovered; the control with the keyboard or pad focus (the
  same `ui_up`/`ui_down` actions) is framed in gold, and its caption lit
  with the menus' cursor (`CursorMark`) before it (`OptionsPanel.Focused`).

## The Flash screens (G5)

The remaster's own PC screens are 19 Flash (Iggy) movies in the archive
(`ffx_data/gamedata/ps3data/flash/`; ffx-rust's docs/formats/pc-ui.md, "The
19 movies"). Without Ruffle (the default build) the host's Iggy players are
null: they draw nothing and call nothing. gd-ffx draws each natively from
what the game tells the movie.

**What the game tells them** (`rust/ffx-gd/src/flash.rs`). Three of the
game's functions are wrapped in the function table at the boot
(`Table::wrap`, as the ESC menu's `crate::esc`; their ports are the
originals the wrappers call):

| Function | Prologue | gd-ffx notes |
|---|---|---|
| `IggyMenu_ctor_00a288f0(menu; path, x, y, w, h, flags)` | `55 8b ec 6a ff` | every movie the game opens: its file name, menu, player (`[[menu + 0x6c]]`) and open order |
| `IggyMenu_callIggyAS3External(menu; target, name, n, values, unused)` (`0x00a28ba0`) | `55 8b ec 8b 41 6c` | every AS3 function the game calls on a movie, with its `IggyDataValue`s as text (booleans, numbers, UTF-8 and UTF-16 strings; arrays, type 9, are left empty) |
| `PhyreScene_isOpeningScreenPlaying()` (`0x00660400`) | `a0 c2 b9 cc 00` | the start-up loop's check: the native opening's `Finish` (below) |

Whether the game draws a movie (asks it to play: Iggy draws only enabled
menus) comes from the host's players' draw counts
(`ffx_host::iggy::Iggy::players`), sampled each Godot frame
(`flash::sample`; a player's counts go with it when the game deletes it).
`FfxGame.flash_movies`, `flash_calls` and `flash_calls_pending` hand them to
C#, where `FlashScreens` takes the calls once a frame and routes them; with
`--flash-log` each movie and call is printed (`ffx flash: ...`).

**The movies' art.** `ffx_import::flash` reads a movie from the player's
archive with Ruffle's SWF reader (the `swf` crate, MIT OR Apache-2.0) and
draws it in software at any scale: shapes (solid, linear, radial and focal
gradient, and bitmap fills; strokes with round joins), morph shapes at a
ratio, sprites' display lists (nested sprites loop from the frame they
were placed), matrices and colour transforms, lossless and JPEG bitmaps
(`DefineBits` with the movie's `JPEGTables`, `DefineBitsJPEG2/3` with
the alpha plane; `jpeg-decoder`, MIT OR Apache-2.0). Not drawn: filters
(`PlaceObject3`'s glows and blurs), masks, blend modes, text, scripts.
`FfxGame.flash_render`, `flash_bounds`, `flash_class` (a `SymbolClass`
name), `flash_label` (a frame label), `flash_alphas` (a depth's alpha frame
by frame) and `flash_bitmap` give it to the views; `ffx-import flash` draws
a character into a PNG (docs/importer.md). Nothing is stored.

| Movie | Native | What the game does, what gd-ffx draws |
|---|---|---|
| `openingscreen.swf` | `OpeningView` | the start-up opening, below |
| `chocobo.swf` | `AutosaveView` | the autosave prompt, below |
| `saveload_jp.swf` (all languages but Korean, Chinese), `saveload_kr.swf`, `saveload_ch.swf` | `SaveLoadView` | the save and load screen, below |
| `box.swf`, `box_kr.swf`, `box_ch.swf` | `BoosterView` (the toast) | the tips: each `OnPlay(0, text)` (`TipSystem_PlayTip`, which `TipSystem_PushTip(id)` and `graphicPushTip*` reach) shows the text the game passed, whoever sent it |
| `speed_2`, `speed_4`, `invincible`, `enemy_0`, `enemy_1`, `autobattle` | `BoosterView` (badges) | the boosters' icons (from `Snapshot.boosters`, "Cursors, boosters, ..." above) |
| `autobattle_disable`, `freecamera`, `time_stop` | `BoosterView` (badges) | never shown by the original (below) |
| `escmenu.swf`, `pausemenu.swf` | `PauseView` | "Cursors, boosters, the ESC menu and the options" above |

**The start-up opening** (`openingscreen.swf`, 1920 x 1080, 263 frames at
29.97 a second: 8.8 s). `PhyreScene_beginRenderOpeningScreen` opens it
(the menu at `PhyreScene + 0x10dc8`), registers
`PhyreScene_onOpeningScreenFinish_00652400` for its `Finish` callback and
sets `g_PhyreSceneOpeningPlaying` (`0x00ccb9c2`); `graphicInitScene` loops
`PInput_Update` and `PhyreScene_renderOpeningScreen` (33 ms a frame) while
`PhyreScene_isOpeningScreenPlaying` answers that byte. The movie has no
key: the original cannot skip it. What it shows (its timeline): black for
5 frames; the SQUARE ENIX logo (a 660 x 70 bitmap, at 616, 505) fading in
over frames 6 to 34, held, fading out over 126 to 155; then the autosave
notice: a 561 x 151 group, a text line (`getLocByStr("Loc_Texture_Saveload_Attention_Before_Game")`,
which `FFX.exe` maps to localisation line 0x1f9: `mov [ebp-8], 0x00b4b4e0;
mov [ebp-4], 0x1f9` at `0x006e96d2`, into the name map `0x00ce6eb0` that
`IggyCallback_lookupName_006e9590` reads) over the running chocobo (sprite
438, the autosave prompt's art), a black cover fading out over frames 156 to
170 and back in from 249. The text line is the movie's own bitmap font
(`bitmapTextField`, with a marquee for long text); gd-ffx draws the game's
two lines (`@el@`) in the UI font, the group centred (where the movie's
script puts it was not read from its ActionScript). gd-ffx boots without
`skip_opening` when going to the title (`GameHost.NativeOpening`: no
developer save, no `--skip-opening`); `OpeningView` starts when the game
opened the movie, takes the logo (the main timeline's frame 34, drawn at
the stage's size) and the chocobo's 8 frames from the player's movie and
the fades from its timeline (`flash_alphas` of depths 1 and 5), and after
frame 263 asks `opening_finish`: at the loop's next check the wrapper calls
`Finish` through the game's dispatcher (`ffx_host::iggy::as3_call` with
the opening's player); the handler clears the byte and the boot goes on (to
the in-game opening, then the title, as before). If no frontend asks, the
wrapper ends it after 30 s of the loop. Measured headless: the game waited
(no update ran), the view played 263 frames, `Finish` sent, the loop went
on; `--selftest` reports it (`start-up opening: logo, autosave notice
(frame 263), the game waited, Finish sent, the start-up loop went on`).

**The autosave prompt** (`chocobo.swf`, 80 x 80 stage, 70 frames).
`PhyreScene_showAutoSavingPrompt` (`0x0066f480`) deletes the last prompt
(`PhyreScene + 0x10ddc`) and opens the movie anew at 1220, 660, 50 x 50
on the 1280 x 720 screen (`IggyPlayerOpenScaled`) when an autosave starts;
the movie has no calls: the running chocobo fades in over 4 frames, runs
(an 8-frame loop of shapes, radial gradients and morph shapes) and fades
out over frames 66 to 70, where it stops. `AutosaveView` plays its 70
frames (drawn from the player's movie at twice the size) each time a new
`chocobo.swf` is opened. Reached: the Besaid check's walk from the beach
(`--selftest-besaid --devsave=7`) changes the map, and the game autosaves.

**The save and load screen** (`saveload_*.swf`). The save machine
(`SaveDataMachine_*`, ffx-rust's docs/formats/save.md) reads the keys
itself and drives the movie only by calls; the movie calls nothing back.
Measured (`--flash-log`): a save sphere's save opens it with
`setMouseVisible(false)`, `showSaveLoad()`, `SetLanguage("JPN")` (always:
the game passes its texts already in its language), `clearSaveLoad()`,
`showPopUpStd("Loading...")`, one `addSaveLoadItem` a row, `setSaveLoadHelpText(help,
"n/200")`, `setSaveLoadFocus("0")`, `hidePopUpStd()`; Up/Down send
`setSaveLoadFocus`; confirm sends `showPopUpWithBtn(question, yes, no,
false)`, and for "Save?" and "Load this file?" a `changeSelectedBtn()` in
the same frame (for "Overwrite?" none); Left/Right send
`changeSelectedBtn()`; Yes sends `showPopUpStd("Saving...")`,
`hidePopUpWithBtn()`, `hidePopUpStd()`, and the game opens the movie anew
with the list as it is now; No sends `hidePopUpWithBtn()`. So the movie's
cursor starts on No and the game moves it to Yes except for the overwrite
(a fourth value of true is taken to start on Yes; not seen). A row's twelve
values: index, slot, place, date, the player's name, play time, three party
members (`_1` .. `_8`, `_0` none), the thumbnail (`m<n>`), a label
("Autosave", gold), and a row text (the new file's row, "Create new save
data", has only that and slot 0). `SaveLoadState` keeps what the calls set;
`SaveLoadView` draws it while the machine's state (`Menus.save_state`) is
neither 0 nor 0xa (the idle state a load leaves), laid out as the
original's screen at 1280 x 720 (the M5 pilot's Iggy pictures of the load
list and its pop-up): the help bar ("HELP", the help, the count under its
end), five 1038 x 100 rows from y 118, 110 apart (label or slot, place,
date at the right; the portraits; name and play time; the thumbnail at the
right end), the cursor on the focused row, the scroll bar, the message box
and the question with Yes and No and the cursor. The portraits (the movie's
`saveload_jp_fla.face_565` sprite at its frame labels), the thumbnails
(classes `m<n>`, JPEG) and the cursor (`saveload_jp_fla.arrow_21`) are drawn
from the player's movie; the backdrop and frames are drawn by gd-ffx (the
movie builds them in its script). `--selftest-save --devsave=7` and
`--selftest-load` (docs/running.md) check saving, the questions' cursors,
the overwrite question's No, cancel, and loading from the title. The save
screen has no delete (the original's calls offer none). The player's name
in the developer saves' rows reads as kana noise in Japanese ("ぅずさてつ"):
the saves hold "Tidus" in the English encoding and the game decodes it in
the Japanese table; the row shows what the game passes.

**Free camera and time stop** (`freecamera.swf`, `time_stop.swf`, one 64 x
64 icon each) and `autobattle_disable.swf` are never shown by the original:
`SetFields_006f7700` stores the first two's paths and overwrites them,
`FreeCamera_handleInput` (`0x00680030`) asks the icon panel for icons 8 and
9 when the debug free camera's capture (`FreeCamera + 0`) or its time stop
(`+0x338`) changes, and `IconPanel_showIcon` takes only 0..6; icon 6 is
asked by nobody. The same function also plays the tips 0x2ed/0x2ee ("Free
Camera On/Off") and 0x2f3/0x2f4 ("Time Stop On/Off"), which the tips box
shows. gd-ffx shows the two icons as badges with the movies' own bitmaps
while the free camera (`0x00ccc830`, `FreeCamera_FreeCamera` makes it on
first use) has them on, read through `FfxGame.Peek` (a stopgap: the
game-state API lacks them; it needs the singleton's `+0` capture counter,
`u32`, and `+0x338` time stop, `u8`). What turns the free camera on in a
retail build was not found (nothing writes `+0x338` in the ported code
looked at); no check reaches it.

**The check** (`--check-flash`, any self-check; `tools/sweep-devsaves.sh`
passes it): at the end the movies the game opened and showed are listed
(`ffx flash: flash movies opened: ...; shown: ...`), and the check fails
when one has no native screen (`FlashScreens.Native`) or a native screen
did not draw what the game showed (no autosave prompt played for a shown
`chocobo.swf`, no list for a shown save screen, no logo for the opening, a
tip not shown).

## The battle results (G4)

`BattleEndView` draws the results screen while API 10's `menus.battle_end`
runs (main-mask bit `0x20000`, slot 0x11): each character row the game
draws (kinds 0 and 5 are not) by the name the player gave, the AP it still
has to count (the game counts them down) and its sphere levels, "Level up!"
while the row's flash counter runs (0xf when the AP reach a sphere level,
down each draw); the gil counted so far of the gil won (and the party's);
the items won named from the kernel tables with their counts, and the gear
won by name. A gear id is `MsGetSaveWeapon`'s (`0xb000 + i` the battle's
spoils, 8 records at `0x02310dde`; `0x7000 + i` 8 more at `0x02310f9e`;
else an inventory slot of `g_Equipment`); the game names it with
`MsGetSaveWeaponName`: `MsWeaponName` of the record's `+0` (its `w_name`
record, chosen when the gear is made) and `+4` (its character), string
`character` of that record in the kernel's `w_name.bin`, or `7 +
character` (the hiragana form, blank in the Japanese table) with the
config's hiragana bit (save `+0x3d0c` bit 3). The state API carries the
ids only, so `FfxGame.gear_record` reads the record from the game's
memory now (`rust/ffx-gd/src/state.rs`, `gear`), -1 for one not in use
(the game's weapon page skips it), and `FfxGame.weapon_name` names it
(`ffx-import`'s `weapon_name`; `ffx-import wname <lang> [kana]` lists the
table). The Japanese table orders some records' names differently from
the English one (record 1: Tidus フラタニティ, then スピア, ロッド where the
English has Staff, Dull Blade); the game reads string `character` either
way, and so does gd-ffx. FFX has no EXP.
`--selftest-battle --to-results` attacks on (confirm tapped) until the
battle is won and the screen runs, lets the AP count down, then taps
confirm once a second until the gil is paid. Measured on the deck's battle
(developer save 7, `--battle=3,0,0`): first Tidus's row with 3 AP (state
0xa), then Tidus and Wakka at 0 AP (0xd), the gil 5 counted after a confirm
(party 665 -> 670), the spoils one Ability Sphere; the states 0xa .. 0xe,
0xf, 0x14 .. 0x1b, then the field. No level-up in that battle: the flash is
untested. No gear is won there either: the gear names are checked on the
worn weapons (`--selftest-menu`: Tidus ロングソード, Wakka オフィシャルボール
in Japanese, the inventory records read from the game) and on the table
(`ffx-import`'s archive test: record 0 "Caladbolg" .. "Godhand", record 1
"Brotherhood"); the spoils lists' records are not seen yet.

## The G0 state read-out

`FfxGame.state()` returns a dictionary read directly from the game's memory
(`rust/ffx-gd/src/state.rs`): the updates and frames run, the game time,
the map, the previous map, the entrance and the story progress
(`g_SaveData`, `0x0112ca90`), and the running event's frame count. The C#
wrapper (`godot/src/FfxGame.cs`) makes it a typed `GameState`; the debug
overlay shows it next to the snapshot.
