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
| R7a | Effects as Godot particles by effect id, audio replacement by id, movies | R1-R3 |
| R7b | The rest: 2D rooms rebuilt in 3D, UI art (done: R7 complete) | R1-R3, R5 |
| R8 | Quality: before/after renders of every map, a status dashboard, regression pictures in the sweep, performance budgets (Steam Deck), the GPU checks (done: the plan's milestones complete) | all |

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
- the Steam Deck's budgets measured on the device (R8: the tools and the budgets; the device is not at hand);
- ~~R4's wizard: the defaults are its first pass; the wizard tunes them per map
  (exposure from the classic picture~~ (R4), the vertex light against added lights).

## R4 in detail

R4, the remaster wizard, is done (2026-11-18; docs/remaster-editor.md, "The
remaster wizard"):

- **A first automated pass per map**, from the **Wizard** dock over the map
  browser's map, its area, the maps the project changes or all maps. Each map's
  classic import is loaded on its own (no scene opened), and its colour textures
  are listed with how the map draws them (`MapTextures`: surfaces, area, which way
  they face, blend, alpha test, additive, scrolling, sky parts, the water's
  shaders).
- **A profile per texture** (`TextureClassifier`): rock, stone and concrete,
  sand and dirt, grass and foliage, wood, metal, fabric, plaster and painted walls,
  or keep classic (sky, water, glows and effects, background paintings, creatures).
  A hand-made scorer: the texture's name (FFX's HD maps name theirs:
  `lchb00_rockwall_06`), how the map draws it, the map's area, and its colour and
  statistics (`TextureFeatures`: OKLab lightness, chroma and hue shares, lightness
  spread, local and coarse contrast, edge density, direction, seams, alpha
  coverage) against each kind's prototype (the medians of its labelled
  textures). Every point is a named reason; the review shows the strongest
  ("sand 3.7: on the ground +1.0, common in bsil +0.4, warm share 0.995 (its 0.98)
  +0.2, ...; then wood 1.5").
- **The materials** by those profiles, through R2b's run (`QueueBake`; the GPU
  when the Materials tab uses it, else the CPU), in the background with progress
  and cancel; their files content-named in the texture folders, no entry written.
- **The lighting from the game's look**: R3's defaults (the sun from the game's
  key light, its ambient, fog and a sky in its colours), VoxelGI (baked on the
  player's machine, drawn at medium and high quality, over the map's middle at
  most 1,200 x 400 x 1,200 units), and with a renderer the exposure matched to
  the classic picture once (`WizardPass.TuneExposure`: the lightness ratio cubed,
  0.6 to 1.6, kept only if it brings the lightness nearer). Written as
  `lighting.tres` (only what differs from the defaults: `GiMode`, `ToneExposure`).
- **Staged, then reviewed**: per map the before/after pictures rendered offscreen
  in the editor (`WizardRender`: the map in a viewport of its own, from the game's
  camera of R5's capture, else three views over the walk mesh), R3's colour check
  (a warning when it fails), per texture the profile, score, lead over the
  runner-up, reasons and the made albedo and normal; a texture accepted, rejected
  (classic) or made again with another profile; the pictures drawn again; the map
  accepted (the materials, the render path remastered, the status in progress, the
  pass's note added to the notes, `lighting.tres`: one undo step) or discarded.
  Every review action is one undo step; making a pass is none (it changes no
  project file).
- **Batch**: the queue with progress, pause (after the map being made) and resume,
  in this session or the next (`res://remaster/wizard/state.json`, saved after
  each map), 2D-layer maps skipped or noted as kept classic, a summary (maps done,
  materials per profile, failures, the time per map); headless with
  `--remaster-wizard --maps=a,b,c` (`--wizard-resume`, `--wizard-accept`,
  `--wizard-pause-after=N`) in the editor.
- **In the game**: `--selftest-wizard` draws Besaid beach with the editor's
  accepted pass (`--wizard-expect`) or runs the same pass in the game (the
  Windows GPU check, where the editor's files are not at hand).

**The classifier's accuracy** (`--selftest-wizard-classifier --devsave=7`;
the labels: `godot/remaster/classifier-labels.tsv`, 141 texture IDs with kinds
labelled by eye from thumbnails of Besaid, Luca, Macalania, Bevelle, Zanarkand,
Kilika, Mi'ihen, Djose, Guadosalam, Moonflow and Gagazet; no images; ice and snow
left out, no profile fits them):

| | textures | right |
|---|---|---|
| the rules frozen, scored on six areas labelled only afterwards (Kilika, Mi'ihen, Djose, Guadosalam, Moonflow, Gagazet) | 34 | 14 (41 %) |
| the shipped prototypes, leave one area out (each area scored by prototypes fitted to the others: the honest number) | 141 | 77 (55 %) |
| the shipped prototypes, fitted to all labels | 141 | 91 (65 %) |

Per kind, leave one area out (right / labelled; precision): keep 8/14 (8/13),
rock 6/24 (6/22), stone 17/30 (17/35), sand 16/17 (16/20), grass 27/30 (27/29),
wood 1/11 (1/7), metal 0/6 (0/4), fabric 2/5 (2/6), plaster 0/4 (0/5). Grass,
sand and keep classic are reliable; rock against stone is half chance (FFX's
carved stonework and its cliffs share colour and statistics), wood, metal and
plaster are mostly missed (few examples, and Macalania's blue bark and Guadosalam's
roots look like nothing else). The mistakes that matter most are rare: 7 textures
to keep classic given a material, 4 materials kept classic. So the review is
needed, and it is where the scores and reasons are shown.

**Measured** (2026-11-18, the development machine, the editor under Xvfb:
llvmpipe, Compatibility):

| | |
|---|---|
| Besaid beach, a pass | 53 textures, 51 materials (sand 16, grass 20, rock 4, plaster 4, fabric 4, wood 2, stone 1), 2 kept classic; 66 to 78 s (the bakes 37 to 46 s on the GPU pass graph under llvmpipe; the imports and pictures the rest); headless (the CPU) 90 s |
| small maps | slik08 (5 textures) 10.5 s, mtgz03 (4, all sky) 1.7 s |
| Besaid's colour check in the editor's review | dL +0.042, chroma x0.93, hue 4.3°; exposure tuned 1 → 0.8: dL +0.005, x0.94, 2.7° (cells 0.076, 1.6°) |
| the game with the editor's accepted pass (Compatibility, Xvfb) | 50 of 50 materials drawn with their maps; dL -0.006, chroma x0.97, hue 0.9° (cells 0.064, 1.8°) |
| the game running the pass itself (Compatibility, Xvfb) | 51 materials at 512 px in 59 s (the CPU); dL +0.033 → exposure 0.85 → +0.005, x0.96, 1.5° |
| the same on the GPU (the Windows export, Vulkan, Forward+, Intel Arc 140T), VoxelGI drawn | 51 materials in 40 s; dL -0.065 → exposure 1.4 → -0.005, x0.90, 1.9° (cells 0.059, 1.1°); with SDFGI (the first rule): hue 28.8° off, failed; with no GI: -0.002, x0.89, 1.4° |

### Decisions (R4)

- **The pass is staged, not applied.** Making a pass writes no project file but
  the made maps (content-named, unreferenced until accepted, as Apply to queue
  writes them before its undo step) and the wizard's state; accepting a map is
  one undo step that sets the texture entries, the render path, status and notes,
  and writes `lighting.tres`. A discarded pass leaves its maps' files in the
  texture folders (a later bake of the same settings reuses them).
- **A hand-made scorer with prototypes, not a model.** No learned model (none
  needed, no licence question): rules for names, uses and areas, and per kind a
  prototype of features (the medians of its labelled textures), each feature's
  fit a named reason. The prototypes are numbers, fitted to the labels; the labels
  file is texture IDs and kinds (no game data).
- **Honest numbers**: the rules were tuned on Besaid, Luca, Macalania and Bevelle;
  Zanarkand was held out and looked at once (it showed that "wall" says nothing:
  Zanarkand's "wall" textures are ruined facades, so the word went); six more
  areas were labelled after the rules were frozen (41 %). The shipped prototypes
  are fitted to every label, so their number is leave one area out (55 %).
- **"Keep classic"** for sky parts (with scrolling UVs, or facing no ground: the
  sky rule `MapLook.FindSky` took some of Luca's big ground textures too, fixed in
  R7b), the
  water's shaders, additive and soft-alpha blended textures, mostly cut-out
  textures that are not green, and tiny ones (under 32 px). Blended alone is not
  a reason: Besaid's sand is a blended terrain.
- **VoxelGI, not SDFGI.** The first rule chose SDFGI for maps bigger than
  VoxelGI's box (Besaid beach: 6,026 x 1,139 x 6,026 units). On the GPU (the
  Windows export, Vulkan, Intel Arc 140T) SDFGI leaked light over the beach: the
  cliffs sand-yellow, the foliage and the village white, the colour check's hue 29°
  off (the lightness +0.11 before the exposure was tuned). With VoxelGI the same
  pass scored dL -0.005, chroma x0.90, hue 1.9° (after the exposure, from -0.065),
  with none -0.002, x0.89, 1.4°. So the wizard always chooses VoxelGI (a big map has
  GI in its middle only, which elsewhere is none); SDFGI stays a hand choice, and
  its settings for FFX's scale want a look of their own (R3's GPU check had used
  VoxelGI).
- **The exposure is tuned once**, from the review's classic and remastered
  pictures, and kept only when it brought the lightness nearer; the other R3
  defaults stay (they follow the game's look). Without a renderer (headless)
  there are no pictures, no colour check and no tuning.
- **The status becomes in progress** on accept (a status set higher by hand
  stays); "remastered" and "approved" are the reviewer's.
- **The review renders in a viewport of its own**, after Godot imported the made
  maps: a material whose maps did not load drew as plain metal (its metallic
  multiplier alone), which first made the review's picture far darker than the
  game's (since R7b such a material is drawn from its albedo, logged).
- **The state is the project's** (`res://remaster/wizard/state.json`, IDs,
  profiles, scores, the staged entries; gitignored in gd-ffx's checkout); the
  pictures and thumbnails are the player's game (`user://remaster/wizard/`).

Open after R4:

- the classifier: more labelled textures (wood, metal, plaster have 4 to 11),
  regions within an atlas (R2b phase 6), per-area prototypes; ice and snow need a
  profile of their own;
- maps the game has not shown on this machine have no look: their lighting
  follows the defaults' key light and a sky without the game's colours (the
  review says so; play the map once);
- the vertex light against added lights, per-map tuning of the key and ambient
  beyond the exposure;
- the review's pictures need a renderer; the headless batch stages without them;
- a discarded pass's files stay in the texture folders (no clean-up action yet);
- ~~MapLook's sky rule takes Luca's large ground textures as sky~~ (R7b: on
  `lchb00`; the rule requires a group to go round the axis).

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
- ~~the 2D layers drawn in the editor (R7)~~ (R7b: in the rebuilt room).

## R6 in detail

R6, characters, is done but for the art, which is the user's (2026-11-18;
docs/remake.md, "Retargeting" to "The stand-in humanoid"; docs/remaster-editor.md,
"Characters"; docs/importer.md, "Characters for the remaster"):

- **The character browser and edit scene**: the Characters dock lists the
  archive's 872 character models with the game's names (the party's, the
  monsters'), kinds, motions and remake status; a character opens as an edit
  scene with the classic import, its skeleton and its own motions (the
  `chr-motions` import: the motion groups as glTF animations, 140 for Tidus),
  which the dock plays, pauses and scrubs; the remake stands beside it.
- **Higher-detail models with skeletons of their own**: **Assign model...**
  makes a remade scene (`res://remake/chr/<id>.tscn`, a `RemadeCharacter` over
  the user's scene) and its rig (`<id>.rig.tres`, a `CharacterRig`), which the
  remake registry, the convention and the packs layer as G6's scenes.
- **Retargeting to the game's bones**: the automatic mapping by name
  patterns, hierarchy and rest positions (the game's unnamed skeleton labelled
  by its shape and attachment points), the alignment (up, facing, height,
  feet), the rest-pose alignment (a T pose onto the game's A pose), the
  mapping fixed by hand in the dock (one undo step each), per-bone offsets and
  position modes; at run time the game's per-snapshot bone poses drive the
  remake (the rotations, an aim at the next joint, the hips' walk), in the
  editor the classic motions do, through the same `Retargeter`.
- **Attachments**: what the game binds to a hand (weapons: `Actor.parent`,
  `parent_bone`) follows the remade hand or a socket bone, by the party's
  attachment points 4 and 5 and the PS2 skeleton (which has the points the HD
  model lacks).
- **Shaders**: skin (Godot's SSS in skin mode with transmittance), hair
  (strands as hashed alpha or alpha to coverage, anisotropic highlights along
  a flow direction or flow map), eyes (a refracted parallax iris, a wet clear
  coat), cloth; presets in `res://remake/materials/`.
- **Cloth**: per chain the game's cloth (each bone turned by how far the
  game's simulated vertices hang from their skinned direction) or Godot's
  `SpringBoneSimulator3D`, or off; chosen automatically, set per chain.
- **Faces**: a face table maps game elements' poses (the jaw the game opens as
  a character talks) onto blend shapes or bones, ranges editable.
- **The test character**: an original stand-in humanoid made in code
  (`StandInHumanoid`), with its own names and proportions, hair, eyes, a jaw
  and blend shapes, a hood and a ponytail.
- **Checks**: `--selftest-character --devsave=7` (the stand-in on Tidus while
  he walks: bone directions within 0.09° of the game's, joints on the game's
  within 0.0000 units, the hood on the game's cloth, the ponytail on Godot's
  spring bones, the face table driven, the presets; pictures with `--shot`),
  `tools/remaster-editor-check.sh`'s step 5 (the editor, then the game with
  the editor's scene); in the deck battle (`--battle=3,0,0`) the sword on the
  remade socket; on the GPU (the Windows export, Vulkan, Forward+, Intel Arc
  140T) and under Forward+ on lavapipe the same measures, no shader errors;
  `--selftest-remake`, `--selftest-besaid`, `--selftest-battle`,
  `--selftest-mods` and the quick sweep (10 of 10, 0 errors) unchanged.

### Decisions (R6)

- **The rig is a resource beside the scene, the scene a convention's.** A
  remade character is `res://remake/chr/<id>.tscn` with a `RemadeCharacter`
  root naming `<id>.rig.tres`: G6's registry, convention and packs choose it as
  any remade scene; a pack replaces a character whole (no per-bone layering:
  a rig is one piece of work). The rig's `Align` is the root's transform.
- **Rotations from the game, proportions from the remade model.** A mapped
  bone takes its element's global rotation relative to their rest poses and
  keeps its own bone lengths; the hips take the game's position (the walk, the
  height of a jump). Every mapped bone is then swung to point at the game's
  next joint, because the game's motions translate joints too (Tidus's wrist
  slides along the forearm: up to 11.7° off without the swing). `Game`
  positions put a bone on the game's joint where contact matters.
- **The rest-pose alignment is computed, not authored.** The remade rest is
  turned, chain by chain, so each mapped bone points where the game's does at
  its rest; hubs (the hips, the chest) and the jaw are not aimed. The offsets
  are numbers in the rig (turns between the user's bones and the game's
  rests): the user's work, fine in a pack; the game's rest itself is not
  stored (it is read from the player's classic import when the rig is made).
- **The game's skeleton is labelled by its shape.** Its bones have no names;
  the labeller takes the lowest leaves as feet, the widest pair as hands, the
  highest leaf between them as the head, the longest pair of each limb as its
  upper and lower parts, and the attachment points as hints. The same code
  labels a remade skeleton without usable names; the check requires both to
  agree on the stand-in.
- **The rig's scale is baked into the remade model when it is driven.**
  Godot's spring bones flip half a turn a frame on a scaled skeleton (its docs
  warn; measured), and a model in metres is scaled about ten times into the
  game's units: the rests, the skins' bind poses and the bounds are scaled
  instead of the root.
- **Game cloth as a deviation.** A remade cloth chain turns by how far the
  game's simulated vertices hang from their skinned direction (the classic
  bind pose turned by the element the chain hangs from), so a remade hood that
  is shaped differently follows the game's swing without taking its shape.
- **Faces from bones; no ffx-rust change.** The snapshot's face animation is
  the jaw (point 13) and the head; the HD party's eyes and mouth also use
  texture animation (`chr_texanim`), which the game-state API does not give
  and gd-ffx does not draw yet: a face row reads bones only for now.
- **Motions in a separate import.** The editor's `chr-motions` import holds the
  model and its motions (22 MB for Tidus); the game's `chr` import stays
  without them.
- **Names from the kernel, where it has them.** The party's from `ply_save`,
  a monster model's from the monster record of its number (a model several
  monsters share shows one name); people, objects and weapons are listed by
  id.

Open after R6:

- the eyes' and mouths' texture animation (blinks, the HD party's mouth
  textures) as face channels: an ffx-rust game-state addition (the
  `g_ChrTexAnim` slot states per character) and drawing it on the classic
  models first;
- picking bones in the 3D view, gizmos for a bone's offset, adding face rows in
  the dock (the rig's inspector does it now);
- the events' and battles' motion groups in the editor (only the character's
  own `resident` groups are played);
- a weapon's own cloth follows the game's hand, not the remade one;
- the remaster render path for characters (the classic map is unlit for
  them: they are drawn with Godot's materials under the classic environment;
  R3's rig lights them on a remastered map);
- real character art (the user's), and a CC0 humanoid in glTF to test an
  import end to end.

## R7a in detail

R7a, effects, audio and movies, is done (2026-11-18; docs/remake.md, "Effects"
and "Replaced sounds and movies"; docs/remaster-editor.md, "Effects, audio and
movies"; docs/architecture.md, "Replacing music, sound effects, voice and
movies"):

- **Effects by stable id.** The extension resolves who made each captured
  effect draw (`fxowner.rs`, from the game's memory at the boundary of the
  update the frame drew): a map's field program, `<level>-fp<n>` (entry `n` of
  the map's effect table, by the ppp objects of its part `g_FpRun + 0x80 n`),
  or a spell overlay, `magic-<id>` (the overlay's image range). On Besaid beach
  all 30 draws have owners: 15 field programs, the save sphere's glow
  `bsil00-fp15` (8 draws), the sea's strips `fp0`..`fp13`.
- **Remade effects in the game.** A scene registered for the id (kind
  `effect`: the convention's `res://remake/effect/<id>.tscn`, `remake.json`,
  packs) is instanced at the effect's place while it draws, following it; its
  classic draws are left out; when the classic effect ends the remake's
  particles stop and it is freed after they died out. Options on a
  `RemadeEffect` root (hide the classic, follow, offset, linger).
- **Recordings for the editor**: each effect the game draws, once a run, its
  draw list over 120 updates (`user://remaster/effects/<id>.fxrec`, an
  index); the player's, never in git.
- **The example**: `res://remake/examples/save_sphere_sparkle.tscn`, sparkles
  and a light of Godot's particles and generated gradients (original), on
  `bsil00-fp15`.
- **Audio by id**: every sound command the game queues is seen at
  `Fmod_PushCommand` (wrapped in the function table, as the Flash screens'
  functions are: no ffx-rust change); a replaced id is silent in the game's mix
  (music and voice by their category and track volumes, a sound effect by its
  own) while Godot plays the replacement at the same moments: music looping
  between loop points, following the game's fades and volumes; sound effects
  at the game's volume for them; voice lines; the options' volumes. Layers:
  `res://remaster/audio/audio.tres` and the packs'.
- **Movies by name**: `PVideoPlaybackWin32_open`/`_terminate` name the movie
  and its end, the manager its frame and pause, the movie's sound stream is
  silenced while Godot shows the replacement (Ogg Theora, its own sound or a
  file or the game's, SubRip subtitles), 16:9 over the picture. An entry for
  `LCI` replaces every language's file (`LCI_jp`, `LCI_us`). Layers:
  `res://remaster/movies/movies.tres` and the packs'.
- **F8** turns every replacement off and on for the run (`--no-replacements`).
- **Editor**: the Media dock: Effects (the recordings, an effect opened with
  its classic replay scrubbable beside the remake, assign, classic), Audio (the
  game's sounds by id with their names from the data: 92 music tracks, 6,774
  sound effects, 8,665 voice lines; the classic sound decoded from the player's
  copy and the replacement played; assign with loop points and volume), Movies
  (the game's 57 movies by name and language; assign a video and its sound,
  preview); one undo step each.
- **Checks**: `--selftest-effects --devsave=7`, `--selftest-audio
  --devsave=7`, `--selftest-movie --devsave=22 --movie-video=FILE`
  (`tools/make-test-movie.sh` makes an original test video with Godot's movie
  writer); `tools/remaster-editor-check.sh`'s step 6 (the editor, then the game
  with its files); `--selftest-besaid --devsave=7`, `--selftest-battle
  --devsave=7 --battle=3,0,0`, `tools/selftest.sh` (the start-up opening),
  `--selftest-mods --no-game` and the quick sweep (10 of 10, 0 errors)
  unchanged; the example's pictures under Xvfb (Compatibility) and on the GPU
  (the Windows export, Vulkan, Forward+, Intel Arc 140T: `--selftest-effects`
  passes there too, the sparkles drawn where the classic glow was).

### Decisions (R7a)

- **An effect's id is its maker's.** The game's draws carry addresses; the
  stable identity is what makes them: a map's field-program entry (the map
  file's table) or a spell overlay. The battle's effect records keep their
  record id (G6's `EffectView`); that id holds a per-character counter, so it
  names one effect only within one battle's run. The battle's shared effects
  and the op system's records get no id yet (they stay classic) rather than a
  guessed one.
- **Resolved in gd-ffx, at the game's boundary.** Reading the game's memory
  from a snapshot observer (on the game's main thread, while the objects the
  frame's draws name still exist) needs no ffx-rust change; gd-ffx already
  reads the game's tables (encounters). An FXDR field for the owner would be
  the cleaner home once ffx-rust's capture wants it.
- **The game keeps playing; Godot plays over it.** Replaced sounds and movies
  are not skipped in the game: its scripts wait on their states and ends, so
  the game plays them silently (volumes held at 0, the movie's stream zeroed)
  and Godot plays the replacement at the same moments. Music and voice are
  silenced by their category and track volumes (one plays at a time); a sound
  effect by its own volume (many overlap). The hooks wrap the game's functions
  in the function table (`Fmod_PushCommand`, `Sg_MainLoop`, the movie player's
  open, terminate and read callback), as `crate::flash` does.
- **Ids as the game queues them.** Music by the FMOD command's id (the battle's
  is 0x10, `1_09_battle_theme`), sound effects by sequence number (Besaid's sea
  is `c355`, sample `50005`), voice by voice id, movies by file name with an
  optional language suffix. Names come from the player's data (the music table
  in `FFX.exe`, the event projects, `voiceidmapper.txt`); nothing is stored.
- **Layers like the textures.** Sounds and movies are resources
  (`RemasterSoundSet`, `RemasterMovieSet`) layered project, packs, run time, per
  id; remade effects are scenes through the remake registry and its
  convention, as characters and maps are. The editor writes at once, one undo
  step each (the file's text before and after).
- **One compare key for R7**: F8, beside F7's render path.
- **Recordings once a run, 120 updates**, like R5's captures: enough to replay
  an effect's look and timing next to a remake; capped at 400,000 vertices.
- **Test media are generated**: the checks' tones are made in code, the test
  video by Godot's own movie writer from a pattern drawn in code
  (`tools/make-test-movie.sh`); nothing downloaded, nothing of the game's.

Open after R7a:

- the classic movie picture: gd-ffx draws none (the null device drops the
  movie's texture uploads); decoding the game's WebM in the extension with
  ffx-rust's `ffx-movie` (its VP8 and WebM readers) and showing it at the
  manager's frame would also give the editor a classic preview;
- effect ids for the battle's shared effects (`bat_eff`) and the op system's
  records (by the effect record that started them), and an owner field in
  ffx-rust's FXDR capture;
- a replaced sound effect's pan, positional sound; the voice list in other
  languages; re-synchronising a replacement video to the game's frame when it
  drifts;
- several recordings per effect (moments, variants), effects in the where-used
  index;
- ffx-rust's docs could take the facts found here (a field program's ppp
  objects hang from its part; Besaid's save sphere glow is entry 15);
- ~~the display flag `0x100` and the documented spell test in `fxowner.rs`~~ (R7b).

## R7b in detail

R7b, 2D rooms rebuilt in 3D and UI art, is done; with R7a, R7 is complete
(2026-11-18; docs/remaster-editor.md, "2D rooms rebuilt in 3D", "UI art";
docs/remake.md, "Rebuilt 2D rooms"; docs/architecture.md, "The 2D layers" and
"The Flash screens"):

- **Rebuild in 3D** (the Remaster dock, on a 2D-layer map): a remade map scene
  (`res://remake/map/<map>.tscn`, G6's path) whose root is a `RebuiltRoom`: a
  `RoomProxy` for each layer of each 2D scene and for the floor, a `Model` group,
  and the editor's guides (the scene camera, R5's to 0.01 units; the walk mesh,
  triggers, entrances), opened. The file holds references only; the layers'
  pictures, the planes and the walk mesh come from the player's archive when it
  opens (the editor, the game) and are kept out of it on save.
- **The proxies**: each layer a plane over its pixel rectangle at view distance
  10 z from its scene camera, its picture projected from the camera
  (`ClassicMaterials.ProjectedLayerShader`, the layer shader's colour, blend,
  depth and cut), so from the game's camera the rebuilt room is the classic
  room; any mesh takes a layer's projection (a proxy's own mesh, or `ffx_layer`
  on the user's), the floor is the walk mesh with the farthest layer.
- **In the game**: the rebuilt room drawn with the game's camera and 2D window
  (the squeeze and pan of `GameProjection`), the characters on the walk mesh as
  on any map, the proxies following the game's 2D scene and layer flags; F7 to
  the classic layers and back. The camera stays the game's fixed one: the game
  has no other view of these rooms.
- **Alignment**: R5's check on the rebuilt scene (the floor on the walk mesh: 0
  issues on `ptkl14`; sunk 5 units: 50 of 50 floating); the picture from the
  game's camera against the classic layers.
- **UI art by id**: a screen asks for each piece of a Flash movie's art by its id
  (`SymbolClass` name, `#<character>`, `@<label>`); a layer
  (`res://remaster/ui/ui.tres`, packs', `--ui-art-file=`) gives it an image, drawn
  at the classic piece's size; fonts by role (`ui`, `message`) under the screens'
  `FontVariation`s, the system's behind them. The save screen (cursor, portraits,
  thumbnails), the booster badges and the opening's logo ask by id. F8 classic.
- **The UI Art dock**: the 19 movies, a movie's art ids (classes, labels,
  bitmaps, the timeline) with thumbnails drawn from the player's copy in memory,
  assign an image (every language's movie or one), classic, the UI font, a
  preview (the piece, and the save screen's row); one undo step each.
- **Follow-ups**: `fxowner.rs` counts display flag `0x100` with the sticking bits
  as placed by the display record's matrix, and tests the spell's draws as
  ffx-rust documents (the object, or its birth record's program list: object
  `+8`, then `+0`); R3's sky rule requires a group to go round the axis (6 of 8
  sectors), which Luca's promenade does not; a material whose maps did not load
  is drawn from its albedo (else the classic colour texture) with a log line, not
  as metal.
- **Checks**: `--selftest-rebuild --devsave=14` (also 12, 52; pictures with
  `--shot`, the GPU through `tools/compare-shot-win.sh`), `--selftest-ui-art
  --devsave=7`, `--selftest-sky --devsave=22` (29, 7; `--sky-level=`), the
  materials check's broken material, `tools/remaster-editor-check.sh`'s step 8
  (the editor, then the game with its files).

**Measured** (2026-11-18, Xvfb, Compatibility, the development machine):

| | |
|---|---|
| rebuilt against classic, the picture from the game's camera, the views held at one moment (mean absolute difference, 0..255) | `ptkl14` 0.01 (5 layers), `slik04` 0.00 (25 layers, 3 scenes; it has a 3D model too), `kami01` 0.05 (22 layers, 2 scenes); rebuilt against itself 0.00 |
| the same on the GPU (the Windows export, Vulkan, Forward+, Intel Arc Pro 140T) | `ptkl14` 0.00 (the check passes there too) |
| the same without the hold (the game moving on between pictures) | `ptkl14` 1.75 against 1.50 rebuilt-to-rebuilt; `slik04` 19.2 against 14.1 (the ship's sway) |
| the game's camera against the scene camera the planes were made from | 0.000 units, 0.000°, field of view 0.000° (`ptkl14`) |
| R3's sky rule, ground kept classic as sky (level and within 30 units of the walk mesh) | `lchb00` before 11,028 units² of 74,501 (14.8 %; `lchb00_brickground_07`, 4 sky meshes), after 0 (3 sky meshes); `lchb02` (save 22) 0 and `lchb08` (save 29) 0 before and after (4 and 12 sky meshes, unchanged); `bsil00` 0, its 5 sky meshes kept |
| Besaid's field programs' display flags | when the beach starts all 15 entries 0x0 (the save sphere's entry 15 too: at the origin, its draws at the sphere); once the player uses the save sphere (the save, UX and classifier checks), entry 15 and the sphere's entry 16 (a `bat_eff` texture) have 0x1 (stuck to a character); none with `0x100` over the quick sweep's 10 saves and the checks |

### Decisions (R7b)

- **References, not geometry.** A rebuilt room's file names layers by 2D scene and
  layer id and the floor by its scene; the planes, the walk mesh, the pictures and
  the materials are made from the player's archive each time the scene opens, as
  R5's game-data nodes are, and stripped on save (`NotificationEditorPreSave`). A
  mesh the user gives a proxy, or tags with `ffx_layer`, is the user's and saved.
- **Planes at 10 z, projected.** The layer pass draws a layer at view distance 10 z
  of the main camera; a plane there, facing the scene camera, is where the layer's
  pixels are and as deep. The picture is projected from the scene camera onto
  whatever mesh carries it, so moving or reshaping a proxy keeps its look from the
  game's camera (perspective-correct, the same UVs: 0.00 to 0.05/255).
- **The floor draws without depth, first.** From the game's camera the layers
  cover it (the picture unchanged); from elsewhere it shows the room's floor; the
  characters stand on it because the game walks them on the walk mesh it is made
  of.
- **The scene camera from the map file, `map_pan` from the game.** The file's
  camera is R5's (the game's to 0.01 units); the map's own term of the 2D window
  (`+0xf9c0`), which the main camera has and the layer pass not, is read in the
  game when the room is built (0 on the maps measured) and taken as 0 in the
  editor.
- **F7 between the rebuilt room and the classic layers.** A rebuilt room is drawn
  unless the render path is classic, as a remade map is drawn whatever the path;
  F7 on it sets `classic`, then `map` (each map's own path) again.
- **The game's camera only.** The 2D rooms have a fixed camera in the game; a
  rebuilt room is drawn from it and no other view is offered in the game (the
  editor's views are free).
- **Making the scene is not an undo step**: like Godot's new scene; an existing
  scene is opened, never overwritten.
- **UI art by the ids the screens ask for.** The native screens draw pieces of the
  game's movies; each piece's id is its `SymbolClass` name or character id with a
  frame or label: stable, the movie's own. A replacement is an image drawn in the
  classic piece's place and size (layout stays the screen's); a movie entry
  without its language (`saveload`) covers every language's file, as R7a's movies
  do. Fonts are replaced by role under `FontVariation`s, the system's fonts behind
  them for the glyphs a font lacks (the game's Japanese text).
- **Sky round the axis, not by normals.** Luca's promenade passed the reach tests;
  the meshes' normals do not tell sky from ground (Luca's dome has all of them
  up), so a sky group must have vertices in 6 of the 8 sectors round the vertical
  axis (Besaid's cloud quarters, one texture, all 8).
- **0x100 counted, the workaround kept.** No field-program entry seen has
  `0x100`; Besaid's save sphere glow (entry 15) starts with flags 0x0 at the
  table's origin while its draws are at the sphere, and gets the sticking bit 1
  only once the sphere is used: before that its program places its draws itself,
  so `EffectRemakeView`'s rule (the table place only when it is not the origin
  and near the draws) stays; stuck or matrix-placed entries take their draws'
  centre.
- **The spell test as documented.** The object, or its birth record's program list
  (object `+8`, then `+0`); before, the birth record itself was tested. ffx-rust's
  census found the two documented tests never disagree (4,680 entries); no spell
  was cast in gd-ffx's checks to compare the old shortcut.
- **A broken material is drawn from its albedo.** An ORM material without its ORM
  map (metallic 1 times nothing: plain metal), or a normal or height map switched
  on without its texture (what Godot leaves of a material whose files are gone or
  not imported yet; once they are imported the material is made again): logged once, drawn from its
  albedo, else the classic colour texture.
- **No ffx-rust change**: the layers' depths and rectangles are in the 2D import,
  the cameras in R5's game data, the art ids in the movies (`flash_symbols`, gd-ffx's
  extension).

Open after R7b:

- modelling the rooms (the user's art): the planes are the starting point;
- the layers' texture animation and the scripts' layer moves
  (`changeLayerOffset`/`Scale`) in a rebuilt room (as in the classic one);
- the remaster render path for a rebuilt room's own lights (its proxies are
  unshaded, the classic picture);
- UI art for the autosave chocobo and the opening's chocobo and notice, per-screen
  fonts, the Flash movies' own bitmap text;
- a spell cast in a check, to see the documented test on gd-ffx's side;
- a field-program entry with `0x100` (none seen; `rcbgFPmRestart`'s maps).

## R8 in detail

R8, quality, is done; with it every milestone of the plan is (2026-11-18;
docs/remaster-editor.md, "Before/after renders", "The status dashboard";
docs/running.md, "Regression pictures", "Budgets", "The GPU checks";
docs/building.md, "Privacy and the quick gates"):

- **Before/after renders of every map** (`RenderBatch`): in the editor, offscreen, a
  map after another. A 3D map is drawn classic (nothing of the project's), then
  with the project's overrides, textures, materials and lighting on the
  remastered path, from the game's camera of R5's capture or three views over
  the walk mesh, with R3's colour check. A 2D-layer map shows its classic layers
  from each scene camera, and its rebuilt room from those cameras and from above
  where the project has one, with the mean difference. Headless-capable under
  Xvfb (`--remaster-renders --maps=...|changed|all`), resumable (`renders.json`
  saved after every map; a map drawn is kept unless the project changed it),
  the time per map kept; into `user://remaster/renders/` or `--renders-dir`.
- **The status dashboard** (`StatusDock`, a bottom panel; `RemasterStatus`): every
  map with its status, what the project changes on it, its renders' thumbnail
  and colour check, its budgets; filters, sorting, a summary of the project's
  progress; **Render** and **Report** (`tools/remaster-report.sh`: an HTML page
  with the pictures beside it, outside git, links checked).
- **Regression pictures in the sweep** (`tools/sweep-devsaves.sh --pictures`):
  each save's picture under Xvfb, held at one game update (the game slowed to a
  quarter for the last 30 updates so the views hold that update itself), then
  the same moment without the game's characters, compared with a baseline kept
  outside git (`~/.cache/gd-ffx/sweep-baseline/`; `accept` takes the last run).
- **Performance budgets** (`tools/budget.sh`, `tools/compare/budget.py`): per map,
  classic and remastered, at the Deck preset and at desktop settings, frame and
  GPU ms, draw calls, texture and video memory, the GI's cost; on the Windows
  GPU through the export, and on llvmpipe (marked so); the budgets per preset in
  docs/running.md, the results in the dashboard.
- **The GPU checks in one go** (`tools/gpu-checks.sh`): the classic picture score,
  the lighting colour check, the material pipeline's parity and speed, the
  character, the rebuilt room, the effects, on the Windows export with Vulkan,
  silent and behind every window; a summary table.
- **A privacy check** (`tools/check-privacy.py`) and **the quick gates**
  (`tools/check-all.sh`) before every push.

R8_PLAN_NUMBERS

### Decisions (R8)

- **The renders are the editor's, not the game's.** A map is drawn from its
  classic import in an offscreen viewport, as the wizard's review does (its
  viewpoints shared), so every map can be drawn without a developer save there.
  The game's own picture of a map is the sweep's (the saves). Script-moved parts stand where the
  file puts them, and moved overrides are offsets on that place. The game's
  camera comes from R5's capture when the game has shown the map on this
  machine.
- **"Before" is the classic game, "after" is the remastered path.** The before
  picture binds none of the project's textures (`RemasterTextures.Suspended`),
  the after picture is always the lit path with the project's materials and
  lighting (or R3's defaults), whatever the map's own render path. So an
  untouched map shows what the remaster's starting point gives it, and the
  colour check measures every map the same way. A remade map scene replaces the
  classic map in the after picture.
- **2D rooms from their fixed cameras.** The game has no other view of them. The
  classic picture is a rebuilt room made from the layers, which from the scene
  camera is the classic room (R7b: 0.00 to 0.05/255). A project's rebuilt room
  is drawn from the same cameras, plus one view from above and behind, where
  its model shows.
- **Resumable by record.** `renders.json` keeps what the project changed on the
  map when it was drawn. A later batch keeps a map whose record still says the
  same and draws it again otherwise; `--renders-again` draws all.
- **A bottom panel for the dashboard.** Its table wants width; the Materials tab
  holds the plugin's one main screen and the side docks are full.
- **Only what the files tie to a map is per map.** Overrides, additions,
  lighting, rebuilt rooms and remade scenes are per map. Textures and materials
  go per map through the where-used index, effects by their field-program id
  `<map>-fp<n>`. Remade characters, sounds, movies and UI art belong to no map
  in the project's files, so they are counted in the summary only.
- **The sweep's pictures hold one update and leave the NPCs out.** R7b's hold
  froze the views at one moment within a run. Two runs freeze at different
  updates (1.35/255 on Besaid two updates apart), so the hold waits for the
  baseline's update, slowing the game for the last 30 updates (ffx-host's
  clock) so the views see it, and draws that snapshot itself (alpha 1). The party
  and the map are then the same to 0.02/255, but the NPCs' wandering is not
  (Kilika's running child, 1.63/255 at the same update), so the compared picture
  leaves the characters out and the whole picture is reported beside it. The
  threshold is 0.5/255.
- **Budgets held on another GPU are estimates.** No Deck is at hand. The Deck
  preset is measured on the Intel Arc 140T (Vulkan, the Windows export) and held
  against the Deck's 60 fps divided by an estimated factor (GPU 3, CPU 2: FP32
  throughput and single-thread speed). llvmpipe and lavapipe numbers get no
  verdict. Budgets measure uncapped (vsync and the Deck's 60 fps cap off), so the
  frame time is headroom, not the cap.
- **The GI's cost is a difference.** The remastered desktop run with VoxelGI
  against the same without GI (`--gi=`, a run-time override of the map's GI). At
  the Deck's low quality there is no GI.
- **The privacy check reuses the export's patterns** (`tools/export-files.py`):
  the names come from the environment at run time, so the check names no one.
  In binary files an address counts only when it reads as one: images hold
  random `x@y.z` bytes. Git history is not rewritten.

### Open after R8

- real Steam Deck measurements (the device); the Deck factor checked against them;
- renders with the script-moved parts where the game puts them (R5's capture
  holds their worlds), several captures per map, the battle stages' cameras;
- the renders' views of a map without a capture are three generic views, which
  can miss the map (a small room drawn from outside);
- a texture's or a character's own dashboard (per texture: the maps it is on,
  its material, its profile), characters and sounds by area once the game's
  data ties them to maps;
- the regression pictures of battles and of the remastered path
  (`FFX_SWEEP_ARGS=--render-path=remastered` works; no baseline is kept for it);
- GPU budgets of every map (the quick sweep's ten saves, then all), and on
  other GPUs.

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
  checked against the Steam Deck preset (R8: measured on the development
  machine's GPU; the Deck itself next).
