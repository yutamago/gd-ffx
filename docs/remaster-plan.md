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
| R3 | Lighting and atmosphere: the game's lights and fog as editable objects, Godot lights, sky, GI (SDFGI/VoxelGI/lightmaps), volumetric fog, reflection probes, settings per map and per volume | R2 |
| R4 | The remaster wizard: a first automated pass per map (de-light, PBR, sun from the game's light, sky, GI) with a review step; batch over all maps | R3 |
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

Open for R1b, the editor plugin (`godot/addons/ffx_remaster`):

- the dock: the map browser over `MapCatalogue` (search, areas, status,
  thumbnails; the catalogue needs the archive open in the editor, so the
  plugin starts `ffx-gd`'s classic importer there);
- "open": an editable scene of the classic map (imported, scaled by 10,
  every part a node named and tagged by its ID, a segment as its own node
  so it can be picked), the current overrides applied and shown as edits;
- edits by Godot's gizmos and inspector turned into `PartOverride`s (a
  move into an offset relative to the file's placement, a delete into
  `Remove`, a hidden node into `Hidden`, a dropped scene onto a part into
  `Replace`, a new node into `additions.tscn` with its anchor), one undo
  step each; save writes `overrides.tres` and `additions.tscn`; the status
  and notes in the inspector;
- a project index (`res://remaster/project.tres`) if per-map files are not
  enough (statuses without overrides, thumbnails);
- play from the editor: start the game at the map (a developer save there,
  or a jump) with the edits live;
- the render path is stored but not used until R2.

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
