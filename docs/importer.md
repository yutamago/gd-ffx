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
  - **Characters** by model name (`Actor.model`, e.g. `c001`): `.../chr/<kind>/<name>/mdl/d3d11/<name>.dae.phyre`
    (`c` party, `m` monsters, `n` NPCs, `s` aeons, `w` weapons, `f` objects).
- `rust/ffx-gd` runs the imports on background threads
  (`FfxGame.classic_request`) into the cache (`user://classic` by default,
  `--classic=PATH` or the project setting `ffx/classic/cache_dir`); an import
  that is there already is reused.
- `godot/src/ClassicLibrary.cs` asks for what the snapshots name and loads
  finished `.glb` files at run time (`GltfDocument`); `StateView3D` shows
  them, through `ContentResolver`, which takes a remade scene instead when
  one is registered for the id (docs/remake.md); then the classic import is
  never asked for.

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
cargo run --release -p ffx-import -- --game-dir <game> devsaves      # the developers' saves: map, entrance, scenario
```

`--game-dir` defaults to `FFX_GAME_DIR`.

## Not yet

- Maps whose level is not a 3D map model in the archive (the title's
  `/map/titl/...`) show nothing; the 2D layer maps and the PS2 map camera
  file are not read.
- No collision mesh (the game does the walking), no lights or fog from the
  files, no water, morphing meshes, texture animation or effects; the
  materials are base colour only.
- Big maps load on the main thread (a pause of a second or two the first
  time).
