# The remaster plan

gd-ffx's goal after G0-G6: a full AAA remaster of FFX with Godot as the
engine, made piece by piece. The game keeps running (ffx-rust); what it
draws is replaced, map by map and asset by asset, until everything is
remade. Every replaced piece can be compared with the classic one at any
time.

This builds on ffx-remix (a separate project by the same author with a
custom renderer): its ideas carry over (stable part IDs, per-map edit
operations in layers, texture/material overrides, PBR generators, the
settings stack, baking, a "remaster this map" wizard), its renderer does
not: Godot is the renderer and the editor.

## Decisions (2026-11-17)

- **Edits are Godot-native.** Per-map override scenes and resources
  (`.tscn`/`.tres`) keyed by stable part IDs, edited with Godot's own
  gizmos, inspector and undo, diffable in git, shipped as `.pck` packs in
  the existing load order. ffx-remix's schemas are ported in spirit, not
  file for file.
- **The render path is per map: classic or remastered.** A map not yet
  remastered keeps the exact classic look (the game's gamma-space shaders,
  docs/architecture.md "Colours"). A remastered map uses Godot's physically
  based pipeline: PBR materials, real lights, GI, sky, volumetric fog. The
  editor and the game can toggle a map between the two to compare.
- **Foundation first** (R1), then textures and materials (R2), then
  lighting (R3).

## Rules that stay

- No game data in git or in packs made from it: a remaster pack holds
  only original work (new meshes, textures, materials, lights, edits) and
  references to the player's classic content by ID; the classic content is
  imported from the player's copy at run time.
- The game's logic stays the game's: walkmesh, events, scripts, battles
  run in ffx-rust; a remastered map must line up with where the game puts
  characters (R5 gives the tools to check).

## Milestones

| | What | Builds on |
|---|---|---|
| R1 | Foundation: stable part IDs, the map catalogue with status, the editor plugin (map browser, open a map, edit/delete/add parts, save), overrides applied in the game, play from the editor | G6 |
| R2 | Textures and materials: replacement by texture ID, where-used, upscaling, PBR materials per texture (generators, optional AI), batch conversion, the per-map render path | R1 |
| R2b | The material workbench: a Materials tab with one texture and one map at a time, a big lit preview that changes as a slider moves, every map Godot's materials take, GPU generators, profiles applied to a queue (docs/remaster-materials.md) | R2, R3 |
| R3 | Lighting and atmosphere: the game's lights and fog as editable objects, Godot lights, sky, GI (SDFGI/VoxelGI/lightmaps), volumetric fog, reflection probes, settings per map and per volume | R2 |
| R4 | The remaster wizard: a first automated pass per map (de-light, PBR by a profile chosen per texture, sun from the game's light, sky, GI) with a review step; batch over all maps | R2b, R3 |
| R5 | The game's data in the editor: walkmesh and collision, triggers, entrances and events, script-moved objects, map cameras; checks that remade geometry lines up | R1 |
| R6 | Characters: higher-detail models, retargeting to the game's bones, skin/hair/eye shaders, cloth (the game's simulation or Godot's), faces | R1 |
| R7 | The rest: effects as Godot particles by effect id, 2D rooms rebuilt in 3D, movies, audio replacement by id, UI art | R1-R3 |
| R8 | Quality: before/after renders of every map, a status dashboard, regression pictures in the sweep, performance budgets (Steam Deck), the GPU checks | all |

## R1 in detail

- **Stable part IDs.** Every classic map part (node, instance, segment) and
  every texture gets an ID that survives re-imports, compatible with
  ffx-remix's (`x:/<archive path>#<node>/...`, `x:map/<event>`), exported
  as glTF extras and kept on the Godot nodes.
- **The map catalogue.** All field and battle maps with their level, event,
  place names (the game's own, in its languages) and area, from the
  player's archive (ffx-remix's chain: `eventid.bin`, event scripts,
  `macrodic.dcp`).
- **Status per map:** original, in progress, remastered, approved, plus
  notes; derived from the project (what overrides exist) and set by hand.
- **The editor plugin** (`godot/addons/ffx_remaster`): a dock with the
  map browser (search, areas, status, thumbnails), "open" builds an
  editable scene of the classic map with every part as a node named and
  tagged by its ID; delete, hide, move, replace and add parts; save writes
  the map's override resource; one undo step per edit.
- **Overrides in the game.** ContentResolver applies a map's overrides on
  top of the classic import (remove/hide/transform/replace/add by ID),
  following the game's per-frame object placement and morphs.
- **Play from the editor:** start the game at a map (a developer save
  there, or a jump) to see the edit live.

### R1 progress (2026-11-17)

R1a, the foundation without the editor, is done:

- **Stable part IDs**: the import (format 6) writes ffx-remix's IDs as glTF
  extras and gd-ffx keeps them as node, mesh and material metadata
  (docs/importer.md, "Stable part IDs"; `ffx-import parts`).
- **The map catalogue**: 335 levels (279 field levels from 329 events, 56
  battle stages), 32 areas, place names in the game's eight languages
  (docs/importer.md, "The map catalogue"; `ffx-import maps`,
  `FfxGame.classic_map_catalogue`, `MapCatalogue`).
- **The override format**: `MapOverrides` (`overrides.tres`) and
  `additions.tscn` per map: remove, hide/show, move (offset on the game's
  placement or absolute), replace by a scene, add under an anchor; status,
  notes and render path per map; layered over the project and the packs,
  per part and per field (docs/remake.md, "Overrides").
- **Overrides in the game**, on the classic import and a remade map,
  keeping the game's placement, morphs and visibility, loaded ahead with
  the map; the catalogue entry's derived and hand-set status. Checks:
  `--selftest-overrides --devsave=7`, the layering in `--selftest-mods`.

R1b, the editor plugin (`godot/addons/ffx_remaster`; docs/remaster-editor.md),
is done:

- **The archive in the editor**: the extension's classic functions without
  booting the game (`FfxGame` is a tool class), the folder from the editor
  setting `ffx_remaster/game_dir` or the game's order, the game's classic
  cache.
- **The map browser dock**: the catalogue by area, search (names in every
  language, level, event, map number), names in a chosen language, each
  map's effective status and marks (overrides, additions, remade, PBR),
  only the maps the project changes; status, render path and notes per map
  (one undo step, written into its overrides).
- **Open a map**: an edit scene (`res://remaster/edit/<level>.tscn`, the root
  and the level only, not in git, not exported) that builds the classic
  import in render space, every part a node tagged by its ID, every
  instance's segments as nodes of their own, the classic materials, the
  project's overrides and additions applied; the dock's parts list with
  marks (changed only); the part's inspector section (ID, kind, source,
  textures, its edits, revert, replace, remove).
- **Editing**: Godot's gizmos, inspector, eye and Delete, the plugin's
  remove, revert, replace, notes and add (primitives and lights at a part,
  or any node dropped under a part or the map), one undo step each; save
  (the dock or Ctrl+S) diffs the scene against the classic placement into
  `overrides.tres` and `additions.tscn`, text in a stable order (a save
  without an edit changes no byte), never classic content.
- **Play from the editor**: the project run at a developer save on the map
  (`classic_dev_saves`; else one in its area: the game has no jump to a map).
- **Checks**: `tools/remaster-editor-check.sh`: the editor headless (or under
  Xvfb with pictures) driving the plugin on Besaid beach (catalogue, open,
  move, hide, remove, replace, add, undo/redo, save, reopen, status, play
  plan), the game on developer save 7 applying what the editor saved
  (`--selftest-saved-overrides`: each edit where the editor showed it), and a
  round trip of R1a's hand-written example.

Open after R1b:

- the packs' layers shown in the editor (it edits the project's layer);
- thumbnails in the browser (rendered offscreen, cached in `user://`);
- opening 2D-layer maps (R5: with the game's data only) and remade maps' own scenes;
- a project index (`res://remaster/project.tres`) if per-map files are not
  enough (statuses without overrides, thumbnails);
- a jump to any map (the game has none): play starts at developer saves only;
- ~~the script-moved objects where the game puts them and their morphs~~ (R5: from the game's capture);
- ~~the render path is stored but not used until R2~~ (used: R2).

## R2 in detail

R2, textures and materials, is done (2026-11-17; docs/remaster-editor.md,
"Textures" to "Your work and git"):

- **Replacement by texture ID, project-wide**: `res://remaster/textures/textures.tres`
  (a `RemasterTextureSet`, one `RemasterTexture` per ID: replacement, preset
  colour/cut-out/normal/mask, material, recipe, source, note), each texture's
  files content-named in its folder, the PNGs imported by their preset.
  Drawn wherever the game draws the texture, by the classic shaders on a
  classic map (maps, water, characters, 2D layers) and in the editor, bound
  again at once after an edit (`RemasterTextures.Refresh`).
- **Where used**: every map's textures and parts from the archive
  (`classic_texture_uses`, parsing only), built in the editor a few maps a
  frame (13,352 textures in 276 maps, 6 s), cached in `user://` by the
  archive.
- **PBR materials per texture ID**: ffx-remix's generators ported (MIT, the
  same author: de-lighting, height, normal, roughness, occlusion, metallic,
  emission, Lanczos) make an `ORMMaterial3D` by a `PbrRecipe`, editable in
  the inspector; the batch (a map, an area, the changed maps, all) with a
  review step; upscaling by Lanczos, or ComfyUI when configured (off by
  default).
- **The per-map render path**: a remastered map draws its parts lit with
  the PBR materials (or their colour textures), the game's blend, cull, UV
  sets, scrolls and morphs kept, the sky classic, under a provisional rig
  from the game's look (key light, ambient, sky, fog; AgX); the classic path
  stays pixel-exact (Besaid 11.9 under Compatibility, as main). The compare:
  the editor's 3D toolbar toggle, F7 in the game, the options' Render path.
- **Editor UI**: the Textures dock (browser by map / where used / project,
  the before/after split, uses with a jump to the part, replace, upscale,
  generate, edit, revert), the batch panel, each edit one undo step.
- **Packs**: texture layers through `ModLoader` like the overrides (later
  pack wins per texture ID and field; `Classic` undoes).
- **Checks**: `tools/remaster-editor-check.sh` (R2's steps in the editor,
  then the game with the editor's textures), `--selftest-materials
  --devsave=7` (the replacement classic, the material remastered, F7, a live
  replacement, materials from classic pixels; pictures with `--shot`),
  `--selftest-mods` (the texture layers of two packs).

Open after R2:

- ~~the lighting is provisional~~ (R3);
- where-used covers the maps' 3D models only (characters, 2D layers,
  effects, menus next);
- no PBR model template for ComfyUI yet, and the client is untested against
  a real server;
- the baked vertex light is set per map (R3: `VertexLight`), not per part;
- a recipe's live preview, per-texture recipes in the batch;
- the water, the 2D layers and the characters keep their classic shaders on
  a remastered map (R6, R7).

## R3 in detail

R3, lighting and atmosphere, is done but for the GPU check (2026-11-17;
docs/remaster-editor.md, "Lighting" to "The colour check"):

- **R2's grey remaster fixed, and measured.** The colour check
  (`ColourBalance`, `tools/compare/colour.py`): mean OKLab lightness, chroma
  and hue of the remastered picture against the classic one, over the
  picture and a 4 x 3 grid, within tolerances. R2's rig failed it (Besaid,
  Forward+ on lavapipe: lightness -0.21, chroma x0.35, hue off by 159°). The
  causes: light and ambient colours decoded twice or taken as sRGB (a blue
  sun, a black ambient), the game's blue character lights multiplying the
  yellow sand, half the baked vertex light dropped, AgX, and the refraction
  sea losing its colour under the linear output. Now (Besaid, the defaults):
  Forward+ on lavapipe lightness +0.007, chroma x0.88, hue 2.0° (PBR
  materials: +0.003, x0.87, 2.1°); Compatibility +0.040, x0.93, 3.5°; the
  GPU (D3D12, Intel Arc 140T) +0.006, x0.89, 2.0° (PBR: +0.000, x0.87,
  2.1°), classic scoring 8.9 as on main. The remastered view lost the D3D12
  device in six of seven runs, Vulkan none of four; Windows renders with
  Vulkan, Godot's default (docs/remaster-editor.md, "D3D12 and Vulkan").
- **The game's lighting as editable objects**: `MapLighting`
  (`lighting.tres` beside the overrides; text, stable, layered per file):
  the key light, the ambient, the fog as the game's look gives them (or
  custom), sky (the game's colours, procedural, physical, an HDRI), tone map,
  exposure, glow, SSAO/SSIL/SSR, volumetric fog, GI, the vertex light, the
  blended parts. In the editor: nodes tagged `x:level/<key>#light/key`,
  `#ambient`, `#fog`, `#environment`, `#volume/<name>` (the key light a
  directional light with Godot's gizmo, the rest in the inspector, one undo
  step each), the game's rig as the preview, the game's look from a cache the
  game writes per map (`LookCache`).
- **Volumes**: boxes with their own exposure, ambient, fog, key light,
  saturation, volumetric density, blended at their edges by the camera's
  place.
- **Godot lights and probes**: omni, spot, directional lights (shadows),
  reflection probes and fog volumes through the additions.
- **GI**: SDFGI (high quality), VoxelGI baked on the player's machine at the
  first visit (`user://remaster/bake/`, never shipped: the file holds the
  recipe); LightmapGI not yet (no lightmap UVs).
- **The lighting quality** (options, `--lighting=`): high, medium, low (the
  Deck preset): GI, volumetric fog, screen-space effects, shadows.
- **Compatibility**: no GI, volumetric fog or screen-space effects; the lit
  shaders take the game's colours in gamma there (`ffx_lit_gamma`), the rig
  its own key, ambient and exposure scales (measured, not over-bright).
- **The classic path** unchanged: the classic Besaid picture against main's
  differs within run-to-run noise, in the moving clouds, sea and NPCs only
  (mean absolute difference; Compatibility: 0.23 and 0.03 against two runs of
  main, which differ by 0.21 from each other; Forward+ on lavapipe: 0.53 and
  0.52, main against itself 0.84).
- **Checks**: `--selftest-lighting --devsave=7`, the editor check's lighting
  steps and the game applying them, the quick sweep classic and remastered
  (10 of 10, 0 errors each), the GPU comparison with the colour check.

Open after R3:

- D3D12: the remastered view loses the device on the Intel Arc 140T (cause
  unknown; Windows renders with Vulkan, Godot's default): check again after a Godot or driver
  upgrade, and on other GPUs;
- LightmapGI (lightmap UVs for the maps: an importer step);
- the shore: the game's painted layers blend in gamma; they stay classic by
  default, and Besaid's lagoon is still paler than the classic one;
- volumes by the player's place (now the camera's), per-field layering of
  lighting files, the VoxelGI bake seeing the PBR materials' emission;
- the Steam Deck's budgets measured on the device (R8);
- R4's wizard: the defaults are its first pass; the wizard tunes them per map
  (exposure from the classic picture, the vertex light against added lights).

## R5 in detail

R5, the game's data in the editor, is done (2026-11-18; docs/remaster-editor.md,
"The game's data"; docs/importer.md, "The game's data"):

- **From the archive, no game running**: `ffx-import`'s `gamedata`
  (`Importer::map_game_data`, `FfxGame.classic_map_game_data`, `MapGameData`):
  the walk meshes of the map's `MAP1` file (every page; each triangle's map id
  word), the 2D scenes' camera blocks, and per event the script's entrances
  and workers (type, the constant positions, models, yaws, trigger lines and
  boxes, bindings, ranges, enabled triggers, map changes). Under 0.1 s a map.
  Everything ffx-rust's docs describe and its crates parse (`ffx-script`); no
  ffx-rust change was needed.
- **Walk mesh and collision**: translucent, with edges, coloured by kind (open,
  closed to everyone, to the player, to all but the player, switched by the
  scripts). Checked against the game: the player stands 0.00 units from it on
  Besaid beach, and every shown character on it.
- **Triggers, entrances, events**: line and box triggers and sensors (the
  exits orange and named by the catalogue: "to Besaid - Crossroads (bsil02,
  map 0x14)", the inspector's **Open target**), the entrances and the actors'
  start places with their facing (checked against the game: the player turns
  within 2° of the facing the yaw gives), clickable with their info in the
  inspector's "FFX game data" section.
- **Script-moved objects where the game puts them**: the game captures each
  map it shows (`MapCapture`, `user://remaster/captures/<level>.json`, once a
  run, 2 s after the map came, like R3's looks): the placed parts' worlds, the
  morph values, the camera, the characters. The editor puts those parts there
  (their classic base too: no edit shows, and a move is an offset on the
  game's placement), sets the morphs, shows the game's camera and characters;
  without a capture the file's places.
- **Map cameras**: the 2D scenes' fixed cameras from the files (on `ptkl14` and
  `slik04` the game's camera to 0.01 units and 0.02°) and the game's camera
  from the capture, as Godot cameras (Preview, **View from it**).
- **The alignment check** (`Alignment`): each walkable triangle sampled every
  6 units against the remade geometry (additions, replacements, a remade map's
  scene with **Check scene...**): blocked (a surface within 12 units over the
  walk mesh) or floating (the nearest surface under it more than 2 units
  lower); in the editor as marked triangles and a list, in the game as
  `--selftest-alignment`.
- **2D-layer maps** open in the editor with their game data (no 3D to edit).
- **Checks**: `--selftest-alignment --devsave=7` (the data against the game,
  the capture, the alignment check: an aligned patch of the walk mesh 0
  issues, a sunken one 17 floating, a misplaced box 4 blocked; the example
  remade beach 583 floating and 254 blocked of 839, as a plane fitted to the
  characters is); the editor check's R5 steps (Besaid's data and capture, the
  alignment with a misplaced box, `ptkl14` and its camera against the
  game's); `--selftest-overrides`, `--selftest-besaid`, `--selftest-mods`, the
  quick sweep (10 of 10, 0 errors).

### Decisions (R5)

- **The archive first, the game for what only it knows.** The walk mesh,
  triggers, entrances, actor starts and 2D cameras are read from the files
  (the editor stays a game-free tool); where the scripts put objects, the
  morphs, the follow camera and the characters come from a capture the game
  writes when it shows the map, as R3's looks do. A capture is one moment
  (the birds fly on); it is the player's, never in git.
- **Only what the scripts set with constants.** A worker's position, model,
  trigger shape and map changes are read where every argument is a constant
  (entry 0's first); computed values and later event moves are left out
  rather than guessed. The 3D maps' follow cameras (the camera worker's
  polar set-ups) wait for a later round.
- **A captured part's base is the game's place.** The editor moves the part's
  classic base to where the game had it, so it shows no edit and an edit is an
  offset on the game's placement, exactly as the game applies overrides; a
  save keeps the file's place of an added node when the scene still shows it
  (an anchor far out leaves float noise otherwise).
- **The alignment check is the walk mesh's view.** It judges walkable
  triangles where remade geometry covers them (not the remade geometry
  everywhere), with a tolerance of 2 units and a clearance of 12 (characters
  are about 17 tall), sampled every 6 units so a piece smaller than a
  triangle is still seen. Lights, probes, cameras, fog volumes and nodes with
  `ffx_alignment_skip` are left out.
- **2D-layer maps open read-only** with their game data; drawing their layers
  in the editor is R7's (2D rooms rebuilt in 3D).
- **Game-data nodes are the editor's**: under `GameData`, locked, never saved,
  rebuilt at each opening; toggled per group in the dock.

Open after R5:

- the 3D maps' follow cameras and camera paths (the camera worker's entries:
  `CamSetPolar`, `CamSetPos`, the focus lists), their limits;
- computed trigger and actor positions (variables, spline natives), the
  workers' later moves, the switched triangle kinds by script;
- the alignment check on the other walk-mesh pages, per-triangle encounter,
  sound and water bits shown, thresholds in the dock;
- several captures per map (moments of an event), captures of maps only
  battles show;
- the 2D layers drawn in the editor (R7).

## Beyond the milestones

- **Art direction and references.** A per-area reference board, the
  classic look always one toggle away, and screenshot comparisons, so
  the remaster stays FFX.
- **Asset sourcing.** New art is original work (made or commissioned); AI
  tools (upscaling, PBR estimation) only with models whose licences allow
  it, recorded per asset.
- **Collaboration.** Packs in layers (base remaster, per-area work,
  personal tweaks), text resources for git, a conflict report.
- **Performance.** Budgets per map (draw calls, texture memory, GI cost)
  checked against the Steam Deck preset.
