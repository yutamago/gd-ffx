# The classic importer (G2)

gd-ffx draws the game's own maps and characters until remade ones replace
them. They are converted on the player's machine, from the player's own
copy of the game, into a cache folder of gd-ffx's own; nothing of the game
ships with gd-ffx or goes into git.

## How it works

- `rust/ffx-import` reads the player's `data/FFX_Data.vbf` (read only) with
  ffx-rust's `ffx-vbf`, parses the remaster's PhyreEngine models and
  textures with `ffx-phyre`, and writes each model as a glTF binary
  (`Model::to_glb_with`) with its base colour textures embedded as PNG.
  - **Maps** by the level the game's renderer loaded (the game-state API's
    `World.level`, e.g. `/map/bsil/bsil00/`): `.../map/<area>/<map>/mdl/d3d11/<map>.dae.phyre`.
  - **A map's 2D layers** (kind `map2d`): `.../map/<area>/<map>/2d/mdl/d3d11/<map>.dae.phyre`,
    drawn by `MapLayers` (docs/architecture.md, "The 2D layers"). A map of 2D
    layers only has no 3D model, most 3D maps have no 2D layers: the import
    answers `none: ...`, which is not logged.
  - **Characters** by model name (`Actor.model`, e.g. `c001`): `.../chr/<kind>/<name>/mdl/d3d11/<name>.dae.phyre`
    (`c` party, `m` monsters, `n` NPCs, `s` aeons, `w` weapons, `f` objects).
- `rust/ffx-gd` runs the imports on background threads
  (`FfxGame.classic_request`) into the cache (`user://classic` by default,
  `--classic=PATH` or the project setting `ffx/classic/cache_dir`); an import
  that is there already is reused.
- `godot/src/ClassicLibrary.cs` asks for what the snapshots name and loads
  finished `.glb` files at run time (`GltfDocument`; the extras below are
  read from the file's JSON chunk with System.Text.Json by `GltfExtras`,
  not through Godot's dictionaries, and a mesh's fields set on the Godot
  mesh as integer metadata: docs/running.md, "Garbage collections"; loaded
  with Godot's mesh compression off: docs/architecture.md, "Seams between
  map pieces"); `StateView3D` shows
  them, through `ContentResolver`, which takes a remade scene instead when
  one is registered for the id (docs/remake.md); then the classic import is
  never asked for.

## Material states

The export (`Model::to_glb_with`) keeps each material's parameters and
textures (`extras.ffx_parameters`, `ffx_textures`, `ffx_effect`). The
importer adds what the game draws the material with, read from its effect
variant (`PMaterial.m_effectVariant`, the cluster
`shaders/d3d11/<effect>.fx#<md5>.phyre`) with ffx-rust's `ffx-scene`
(`EffectVariant::parse`), once per variant:

- `ffx_switches`: the variant's switches (`ALPHA_ENABLED`, `DOUBLE_SIDED`,
  `MASK_DEPTH`, `IGNORE_FOG`, `TEXTURE_ENABLED`, `REFRACTION_ENABLED`, ...);
- `ffx_passes`: per render pass (`Opaque`, `OpaqueNoMSAA`, `ZPrePass`,
  `Transparent`, `TransparentNoMSAA`, `postOpaque`, `postTransparent`) its
  D3D11 states: `cull` (`NONE`/`FRONT`/`BACK`), `front_ccw`, `depth_test`,
  `depth_write`, `depth_func`, `alpha_to_coverage`, `blend` (render target
  0: `src`, `dst`, `op` and the alpha ones; `null` when it does not blend),
  and `ps_reads`, the constants its pixel program reads (its DXBC
  reflection: `AlphaThreshold` means an alpha test, `fgEff` fog);
- `ffx_inputs`, `ffx_input_types`: the vertex program's inputs
  (`TEXCOORD4`, ...) and the stream type each reads;
- `ffx_texcoords`: which glTF `TEXCOORD_n` each `TEXCOORD` input reads (the
  instance's segment context binds the water's `TEXCOORD4/6/7` to stream
  sets: `PMeshInstanceSegmentStreamBinding`), from the first primitive
  drawn with the material. A material's other primitives can bind other
  streams or hold their streams in another order (bsil06's ground: one
  instance has UV sets 0 and 1 and binds set 1, its set 0 all zero; another
  has set 1 alone; a third set 0 alone): their `TEXCOORD_n` attributes are
  re-pointed so the material's numbers hold for each (the accessors are
  shared). The map shader reads `TEXCOORD0` from the set this names
  (docs/architecture.md, "Map materials");
- `ffx_wrap`: each texture sampler's address modes (u, v) from the
  material's sampler state (`WRAP`, `CLAMP`, `MIRROR`, `BORDER`).

Each mesh (one per instance) gets its instance's `m_render_order`
(`extras.ffx_render_order`) and, when not 0, `m_DObjKind`
(`ffx_dobj_kind`); also its instance's index (`ffx_instance`) and morph
fields (below), and its segments' model id (`ffx_model_id`, also on each
primitive: `PMeshSegment +0x18`, by which the game places a character's
unskinned meshes). Materials also get `ffx_colors`, which `COLOR_n` each
`COLOR` input reads (as `ffx_texcoords`). The `.glb` carries the importer's format
(`asset.extras.ffx_import`, `ffx_import::FORMAT`, 5 since the cloth); a
cached import of another format is made again (two checkouts of different
formats sharing one cache import each other's models again and again: give
one its own, `--classic=PATH`). docs/architecture.md, "Map materials", says how
gd-ffx draws them.

## Cloth

The game moves a character's hair and clothes with a vertex simulation of
its own (docs/architecture.md, "Cloth"). A `PMeshSegment` it moves has its
PS2 model and piece (`+0x20`, `+0x24`, the low words) other than `0xffff`
(ffx-rust's `ffx_host::cloth` reads them the same way); for a character
(`/chr/` models only) each such primitive gets:

- `extras.ffx_cloth`: `[model, piece, first, count, texcoord]`;
- a vertex number attribute: the mesh's cloth vertices numbered in
  primitive order, vertex `k` of the primitive `first + k`, as
  `TEXCOORD_<texcoord>` (`[first + k, 0]`), the first `TEXCOORD_n` from 2
  the primitive lacks. Godot's glTF loader reads `TEXCOORD_2` and up into
  `CUSTOM0..3`, two to a channel and in order (it stops at the first pair
  with neither), so `TEXCOORD_2` is `CUSTOM0.x`, `_3` `CUSTOM0.z`.

The mesh gets `extras.ffx_cloth_vertices` (its cloth vertices in all). The
export keeps a segment's vertex order, which is the capture's. Tidus (`c001`):
model 0 pieces 0, 1, 2 (68, 28, 22 vertices) and model 1 pieces 1, 2 (16,
64), all in his one mesh (a test on the player's game).

## Morphing meshes

The game morphs some map meshes (MIMe; `MorphManager`). Each
`PMeshInstance` says how: `m_animCt` (`+0x30`, registered only above 0),
`m_mimeCt` (`+0x44`), `m_dObjFlag` (`+0x54`), `m_objID` (`+0x58`): the
mesh's `extras.ffx_anim_ct`, `ffx_mime_ct`, `ffx_dobj_flag` (when not 0)
and `ffx_obj_id`. The weight `w` is per object id (`MorphManager_setWeights`
`0x006abfc0`: a display object's morph value, `SceneObject.morph`).

- **Positions** (`m_mimeCt` 1, 868 instances on 80 maps, every one read):
  `m_dynamicMeshInstance` (`+0x18`) leads to a
  `PModifierNetworkDynamicMesh` over the instance's own mesh. Per segment
  its `PModifierNetworkDynamicMeshSegment` names a `PModifierNetwork`
  (`m_totalInputCount` 2, modifier `Morph23` everywhere) and its first input
  among the mesh's `m_modifierNetworkInputs`; each input is a
  `PDynamicDataBlock` (`m_stride` 12, `m_elementCount` the segment's
  vertices, one `Vertex` stream) whose `m_data` lies in the list's arrays,
  one block after another. Each mesh's segments, inputs and segment inputs
  follow the previous mesh's in their lists. The game writes `in0 (1 - w)
  + in1 w` over the `Vertex` stream (`PMorphModifier_blend2x3`
  `0x005ca0d0`; the file's weights are 1, 0). `in0` is the stored stream on
  every map, so the export adds one glTF morph target, `in1 - in0`
  (`mesh.weights` `[0]`, `extras.ffx_morph_targets` 1); a mesh whose stored
  stream differed would get two, `in0 - stored` and `in1 - stored`, driven
  with `1 - w`, `w`. `rust/ffx-import/src/morph.rs` reads them.
- **UV** (`m_mimeCt` 3, a material with `UV_MORPH`): the vertex program
  reads `TEXCOORD0 + UVMorphWeight (TEXCOORD5 - TEXCOORD0)`
  (`ffx_texcoords` says which set `TEXCOORD5` is).
- **Vertex colour** (`m_mimeCt` 4, `VERTEX_COLOR_MORPH`): `COLOR0 +
  VertexColorMorphWeight (COLOR1 - COLOR0)`. Godot imports no `COLOR_1`, so
  the import writes the `COLOR_n` that `COLOR1` reads as `TEXCOORD_2` (r, g)
  and `TEXCOORD_3` (b, a), Godot's `CUSTOM0`. On the maps looked at
  (`lchb06`) no segment context binds `COLOR1`, so by ffx-scene's rule it
  reads the first colour stream, the same as `COLOR0`: nothing changes, and
  nothing is written.
- **UV offset** (else, `m_dObjFlag` `0x400` and `UV_SCROLL`): `TEXCOORD0 +
  (UvOffsetX, UvOffsetY)`, the object's `morph`, `morph_v`.

`ffx-import morphs <level>` lists a model's morph fields and inputs,
`ffx-import morphs all` counts them over every map model. Besaid's objects
18..32 (`FBXASC0498..0512`) are birds: mesh `_0` is a bird whose two inputs
hold the wings down and up.

## Placing them

- **The map** is written as stored (y up). The game places its instances at
  the file's node worlds times 10 in its render space, which is Godot's
  space: the map node is scaled by 10 (`ClassicLibrary.MapScale`).
- **A character** is written as stored too (y down: the game's character
  space). It sits under the character's root transform (`GameStateView.FieldTransform`
  of `Actor.world`, rotation normalised) with half a turn about z
  (character space to Godot's). Its skeleton is posed every frame from the
  snapshot: the game's element `i` (`Actor.bones[i]`, relative to the
  character) is the bone `SkeletonNode_i`'s pose, interpolated between the
  newest two snapshots. Elements the game leaves at the identity keep the
  model's own pose. Each pose is a global one (relative to the character),
  set in the skeleton's order, parents before children (sorted by depth):
  the element numbers do not put parents first (c001's joints come as 0, 1,
  2, 74, 3, ...), and a parent set after its child would move the child.

## Command line

```bash
cd rust
cargo run --release -p ffx-import -- --game-dir <game> --out <dir> map /map/bsil/bsil00/
cargo run --release -p ffx-import -- --game-dir <game> --out <dir> chr c001 w041
cargo run --release -p ffx-import -- --game-dir <game> --out <dir> map2d /map/kami/kami01/
cargo run --release -p ffx-import -- --game-dir <game> devsaves      # the developers' saves: map, entrance, scenario
cargo run --release -p ffx-import -- --game-dir <game> model /map/kami/kami01/   # a model's nodes, instances, materials
cargo run --release -p ffx-import -- --game-dir <game> effects /map/bsil/bsil00/  # its materials' effect variant states, instances' render orders
cargo run --release -p ffx-import -- --game-dir <game> effect --wgsl TransparentNoMSAA 'PS3Data/Shaders/PhyreDefaultLitShader.fx#DE71...'  # a variant's programs as WGSL (ffx-dxbc)
```

```bash
cargo run --release -p ffx-import -- --game-dir <game> loc us 0x1cd 0x1d0 0x1ce   # localisation kit lines (the config's PC row)
cargo run --release -p ffx-import -- --game-dir <game> wname jp [kana]            # every w_name record's gear names, per character
cargo run --release -p ffx-import -- --game-dir <game> devnames 7                 # a developer save's 18 names, decoded in jp and us
cargo run --release -p ffx-import -- --game-dir <game> exits 0x46 0x14            # a map's event and the maps its script jumps to (docs/architecture.md, "Map transitions")
cargo run --release -p ffx-import -- --game-dir <game> cat <archive path> > /tmp/x # a file's raw bytes
cargo run --release -p ffx-import -- --game-dir <game> flash chocobo.swf          # a Flash movie's stage, frames, bitmaps, main display list
cargo run --release -p ffx-import -- --game-dir <game> flash saveload_jp.swf m26 1 1 /tmp/m26.png   # a character (id or class) at a frame (number or label), scale, cropped (docs/architecture.md, "The Flash screens")
```

The WGSL, the texts, the pictures and the files are reading aids printed
from the player's own files; keep them out of git.

`--game-dir` defaults to `FFX_GAME_DIR`.

## Not yet

- The PS2 map camera file is not read; the 2D layers' texture animation is
  not played.
- No collision mesh (the game does the walking), no lights or fog from the
  files, no texture animation. A colour morph whose `COLOR1` a segment
  context binds to another stream has not been seen (see "Morphing
  meshes"). glTF `TEXCOORD_2` and up are not
  read (no map segment seen has more than two UV sets).
- Big maps load on the main thread (a pause of a second or two the first
  time).
