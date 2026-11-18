# The remaster editor

gd-ffx's editor plugin for the remaster (docs/remaster-plan.md, R1b to R7a):
browse the game's maps, open one as an editable scene of its classic parts,
move, hide, remove, replace and add parts with Godot's own tools, and save
the edits as the map's override files (docs/remake.md, "Overrides"), which
the game applies; replace textures, make physically based materials for them
and draw a map remastered ("Textures" and below), and light it: the game's
lights and fog as nodes, sky, GI, volumes ("Lighting"); see the game's own data on
the map (walk mesh, triggers, entrances, cameras) and check that remade geometry
lines up with it ("The game's data"); remake characters: open one with its motions, assign a
remade model with a skeleton of its own and map it onto the game's ("Characters"); make a first pass over
maps automatically and review it ("The remaster wizard"). What it writes into the
project is IDs and your own work; images made from the game's textures are
your work products too, but never belong in gd-ffx's git ("Your work and
git").

The plugin is `godot/addons/ffx_remaster` (C#, editor only). It is enabled
in `project.godot` and opens with the project.

## Setting up

1. Build the extension and the C# project (docs/building.md), then open
   `godot/` in Godot 4.7 .NET.
2. The game folder: the plugin opens the player's archive by itself when it
   knows one. It looks at the editor setting `ffx_remaster/game_dir` first
   (the dock's **Folder...** sets it), then the game's own order:
   `FFX_GAME_DIR`, the folder the launcher remembered, the project setting
   `ffx/game/game_dir`. The folder is only read.
3. Maps are imported into the game's classic cache (`user://classic`), so
   the editor and the game import each map once between them. The game does
   not have to run: the editor uses the extension's classic functions on
   their own (`FfxGame` is a tool class for this; the plugin keeps it out
   of the scene tree).

## The dock

**Remaster** sits beside the Inspector.

- The top line shows the archive (folder, where the folder came from, how
  many maps and developer saves). **Open** opens it again, **Folder...**
  picks another.
- The catalogue: every field map and battle stage (docs/importer.md, "The
  map catalogue"), grouped by area. **Search** matches the place names in
  every language, the level key (`bsil00`), the level (`/map/bsil/bsil00/`),
  event names (`bsmm0000`) and map numbers (`46`). The language box sets
  the language of the names shown. **Changed** lists only the maps the
  project changes (overrides, a remade scene, a status).
- The **Status** column: the effective status (the one you set; at least
  "InProgress" once the map has overrides or a remade scene) and marks:
  `overrides`, `additions`, `remade`, `PBR` (render path remastered).
- The selected map: its key, place, level, events with their map numbers.
  **Status** (Original, InProgress, Remastered, Approved), **Render**
  (Classic, Remastered: "The render path") and the notes are written into the
  map's `overrides.tres` at once, each change one undo step (the editor's
  global history). A map whose overrides say nothing at all has no file.
- **Open map**, **Save edits**, **Play** with the developer save to start
  at (see "Play from the editor").
- **Parts**: the open map's parts with their edits (moved, hidden, removed,
  replaced, noted) and the added nodes with their anchors. **Changed only**
  (on by default) hides the rest. Click a row to select the part in the
  scene, **Revert** puts the selected part back as the classic import has
  it, **Add at selection** adds a box, sphere, cylinder, omni or spot light
  at the selected part (anchored to it; with nothing selected, at the map's
  root).

## Opening a map

**Open map** (or a double click) writes `res://remaster/edit/<level>.tscn`
(the root and the level only; the folder is gitignored and not exported)
and opens it. The scene's root builds the map from the classic import, then
applies the project's overrides and additions:

```
bsil00                    MapEditRoot (render space, at the origin)
├── ClassicMap            the import's root, x:/.../bsil00.dae.phyre, scaled by 10 as the game places it
│   ├── (unnamed)         the PNode tree: #<node> (FBXASC048_root, ...)
│   ├── FBXASC048_0Shape  an instance, #FBXASC048_0Shape/* (no mesh of its own)
│   │   ├── 0             its segment #FBXASC048_0Shape/0
│   │   └── 1             ...
│   └── ...
└── Additions             added nodes without an anchor (the map's root)
```

- Every part is a node tagged with its ID (metadata `ffx_part`): the model,
  each `PNode`, each instance and each segment of an instance as a child
  node of its own (`0`, `1`, ... in segment order). A click in the 3D view
  picks a segment; its instance is its parent in the scene tree (or the
  inspector's **Instance** button). An edit on the instance moves all its
  segments; an edit on a segment is split off in the game, as the game
  does (docs/remake.md, "In the game").
- The materials are the game's (`ClassicMaterials`: the classic look). The
  map is shown as the file places it, except the parts the game's scripts
  move (Besaid's birds): when the game has shown the map on this machine
  (its capture, "The game's data") they are where the game had them, with
  their morphs; without one they are at their file place. Either way their
  edits are offsets on the game's placement, as the game applies them.
- The map's existing overrides show as edits: removed parts are out of the
  scene (the Parts list keeps them, for Revert), hidden ones hidden, moved
  ones moved, replaced ones drawn by their replacement, added nodes under
  their anchors.
- Maps with 2D layers only open with the game's data alone (the walk mesh,
  triggers, entrances, the scene's fixed camera; "The game's data"): there
  is nothing 3D to edit, and **Save edits** writes nothing for them.

## Editing

| Edit | How | Saved as |
|---|---|---|
| Move, turn, scale | Godot's gizmo or the inspector's Transform on a part | `TransformMode = Offset`: the change on the classic placement in world axes, `B = W'.basis W.basis⁻¹`, `t = W'.origin − W.origin` (render units); a part the file placed absolutely stays `Absolute` |
| Hide | the eye in the scene tree, or Visible in the inspector | `Visibility = Hidden` |
| Remove | Delete in the scene tree, or **Remove** in the part's inspector section | `Remove = Remove` |
| Replace | **Replace...** in the part's section (a `.tscn`, `.scn`, `.glb`); **Unreplace** drops it | `Replace = Scene`, `Scene` (shown in the part's frame, unscaled, as in the game) |
| Add | **Add at selection** in the dock, or drop any node or scene into the scene: under a part it is anchored to that part, under `Additions` or the root to the map | a node of `additions.tscn`, metadata `ffx_anchor` (the part), its place in the anchor's frame (origin and axes, unscaled) |
| Note | **Note** in the part's section | `Note` |
| Revert to classic | **Revert to classic** in the part's section, or **Revert** in the dock | the part's entry goes |

An added node follows its anchor in the game (a box on a bird flies with
it) and in the editor (it is the anchor's child). Its ID is
`x:level/<level>/+base/<name>`; names are made unique on save (the scene's
node renamed too). A node under an added node goes with it.

Every edit is one step of the editor's undo history: Godot's own for the
gizmos, the inspector, the eye and Delete; the plugin's for Remove, Revert,
Replace, notes, Add and the map's status.

The part's inspector section ("FFX part", above Godot's own) shows its ID,
what it is (model, node, instance with its segment count, segment of which
instance), its source (the model's archive path) and textures, and its edits
as the file will say them (offset, turn, scale; hidden; removed; replaced
by).

## Saving

**Save edits** in the dock, or Ctrl+S on the map's scene, writes the
project's layer of the map:

- `res://remaster/maps/<level>/overrides.tres`: the map's `MapOverrides`
  with one `PartOverride` per edited part, sorted by ID; the status,
  render path and notes;
- `res://remaster/maps/<level>/additions.tscn`: the added nodes (none: no
  file).

Both are text, in a stable order, with stable resource IDs (the ones the
file had, else fixed ones derived from the part ID) and without Godot's
per-node `unique_id`: saving again without an edit changes no byte. The
first save of a hand-written file may sort its entries and add Godot's
`uid`s; its meaning stays (the editor check opens R1a's example and saves
it back the same). Kept as they were: fields the editor does not show (an
earlier layer's undo: `Keep`, `Shown`, `Game`, `Original`) where the part
is classic, entries for IDs the map does not have, anchors that are not on
the map. A layer that says nothing (no parts, no additions, status
Original, render Classic, no notes) removes its files.

The map's scene file never holds classic content: saving it keeps the
classic map out (it is rebuilt each time the scene opens). Rebuilding the
C# project in the editor rebuilds the open map too: save first.

## Play from the editor

**Play** saves the open map and runs the project (its main scene, the
launcher, which `--devsave` skips) at the chosen developer save, with the
project's overrides as saved: `-- --devsave=N` goes into
`editor/run/main_run_args` for that run (plus `--game-dir` when only the
editor setting knows the folder).

The choice of saves is every developer save that starts on the map (the
map numbers of its events: `FfxGame.classic_dev_saves`, as
`ffx-import devsaves` prints). Besaid beach has two: 6 (the arrival) and
7 (the beach with the player in control, as the checks use). A map without
one gets a save in the same area, if there is one ("walk there"); the game
has no jump to a map, so other maps are reached by playing there.

## Textures

The **Textures** dock (beside **Remaster**) works on textures by their
stable ID (`x:/.../tex/d3d11/<name>.dds.phyre`; docs/importer.md, "Stable
part IDs"). A texture is shared by every part and map that draws it, so its
replacement and its material are project-wide, not per map.

- **The list**: **Open map** (the textures the open map draws, with how many
  surfaces), **Where used** (every texture of every map: "Where used"), or
  **Project** (the ones the project changes). The search matches the ID and
  the maps using it. **Edits** says `replaced`, `PBR`, the profile the
  material was made with (`⟳ Rock changed` when the profile changed since),
  or the pack a change comes from.
- **The compare**: the classic texture (read from the player's archive) left
  of the split, the replacement or the material's albedo, normal or ORM map
  right of it; the slider moves the split.
- **Used by**: each map drawing it, with its parts; a double click opens the
  map and selects the first part.
- **Replace...**: an image file (PNG, WebP, JPEG) as the texture, wherever it
  is drawn: classic maps too (the classic shaders sample it as they sampled
  the game's, docs/architecture.md, "Map materials"), the water's colour and
  normal textures, characters, 2D layers. Its preset is a cut-out when its
  alpha is all 0 or 255, else colour.
- **Upscale x2**, **x4**: the colour texture (the replacement, else the
  classic one) upscaled as the replacement ("Upscaling").
- **Materialize**: the selected textures (several: Ctrl or Shift+click) into
  the Materials tab's queue, where materials are made ("The Materials tab").
- **Edit material**: the material in the inspector.
- **Revert**: back to the classic texture (no replacement, no material).
- Below: the textures whose profile changed since they were made ("Rock
  changed: 37 textures") and **Make again** (in the Materials tab: the
  profile as it is now, each texture's own exceptions, one undo step).

Since R2b's phase 3 the dock is a browser: the recipe sliders, **Generate
material** and the batch went; materials are made in the Materials tab, with
a profile, one texture (**Save**) or the whole queue (**Apply to queue**).

Every edit is one step of the editor's (global) undo history; undo and redo
touch no file, the images being written once under their content's name.

### The files

The project's texture layer is `res://remaster/textures/` (the editor check
uses `--remaster-textures=DIR`):

```
res://remaster/textures/
├── textures.tres                       RemasterTextureSet: one RemasterTexture per texture ID, sorted
└── map/bsil/bsil00/<name>/             each texture's folder (RemasterTextures.Folder)
    ├── replacement.<hash>.png          the replacement, imported by its preset
    ├── albedo.<hash>.png               the material's maps
    ├── normal.<hash>.png, orm.<hash>.png, height.<hash>.png, emission.<hash>.png
    └── material.<hash>.tres            an ORMMaterial3D over them (text, the maps by path)
```

A `RemasterTexture` (`godot/src/Remaster/RemasterTexture.cs`): `Id`, `Mode`
(`Edit`; `Classic` undoes an earlier layer's), `Replacement` (an image or
texture resource), `Preset` (`Colour`, `CutOut`, `Normal`, `Mask`), `Material`
(an `ORMMaterial3D`, a `StandardMaterial3D` or a `ShaderMaterial` of your
own), `Recipe` (every setting the material was made with, `PbrRecipe`),
`Profile` (the material profile, by name), `ProfileHash` (the profile's
content hash when the maps were made), `Exceptions` (the settings that
differed from the profile: setting to value), `Source` (where the
replacement comes from: by hand, upscaled how, the tool, model and licence of
an AI step) and `Note`. Paths are relative to the index's folder (or
`res://`, `user://`, absolute), so the folder moves into a pack as it is.
The index is text in a stable order with stable resource IDs: a save
without an edit changes no byte.

The PNGs are imported by Godot with their preset (lossless with mipmaps; a
normal map as one; a colour or cut-out texture with its alpha border fixed;
no 3D detection). A new texture's folder is new to Godot's file system: the
plugin asks for a scan, imports what it leaves, and reloads the textures
when the import is done; until then they load from the PNGs themselves.

### Layers

Like the overrides (docs/remake.md, "Layers"), per texture ID and per field
(the replacement, the material), the last layer that sets one wins:

1. the project's `res://remaster/textures/textures.tres` (layer `base`);
2. each mounted pack's `res://mods/<pack>/remaster/textures/textures.tres`,
   in load order;
3. sets registered at run time (`RemasterTextures.Register`; the checks).

`--no-overrides` or `--no-textures` turns them off. The merge is made on
the main thread (`RemasterTextures.Current`) and read by the loading
workers, which bind the replacements as they make the classic materials;
the images load on first use.

## Where used

Which maps and parts draw each texture, over every map of the catalogue
with a 3D model: the extension's `classic_texture_uses(level)` parses the
map's model (no glTF, no pixels; ffx-import's `texture_uses`: each segment's
material, the instance's material set first) and the plugin reads a few
maps a frame after the archive opens (on the Steam copy, 2026-11-17: 13,352
textures in 276 maps, 6 s). It is cached in
`user://remaster/texture-uses.json` (never in git), keyed by the game folder
and `FFX_Data.vbf`'s size and time; **Index** in the dock builds it again.
Characters, menus and effects are not in it yet.

## Materials

A texture's physically based material is made from its colour texture (the
replacement, else the classic one) in the Materials tab (below), by the
workbench's pass graph (R2b; docs/remaster-materials.md, "The steps"), as a
`PbrRecipe`'s settings say, usually a material profile's. R2's own chain
(the generators ported from ffx-remix, MIT, the same author;
`godot/src/Remaster/Texturing/`: `MapGenerator`, `Lanczos`) was the first
form of it: the dock's **Generate material** with these settings, which an
R2-era entry still holds (`Version` 1: loaded migrated, "Profiles and the
queue"):

| Step | Recipe | How |
|---|---|---|
| upscale | `Upscale` 1, 2, 4; `Upscaler` lanczos or comfyui; `MaxSide` | Lanczos-3 in linear light, premultiplied ("Upscaling") |
| de-lighting (albedo) | `Delight` 0..1, `DelightRadius` | the painted light divided out (a blur and a self-guided filter of the log luminance), occlusion and cavities lifted, highlights squeezed, colour casts flattened; hue and mean brightness kept |
| height | `HeightContrast`; `HeightScale` (parallax depth, 0: none) | blurred perceptual luminance |
| normal | `NormalStrength`, `NormalBlur` | Sobel slope of the height, OpenGL convention (green up, as Godot reads it) |
| roughness | `RoughnessBase`, `RoughnessRange` | darker and busier is rougher |
| occlusion | `OcclusionRadius`, `OcclusionStrength` | horizon-based, from the height |
| metallic | `Metallic` | a constant (FFX's textures show little bare metal: paint it) |
| emission | `EmissionThreshold` (above 1: none) | the colour where the brightest channel passes it |

The result is an `ORMMaterial3D` (occlusion, roughness, metallic in one
texture) with the albedo, normal and height maps: Godot's own material,
**Edit material** opens it in the inspector (its preview, every parameter).
A `StandardMaterial3D` or a `ShaderMaterial` of your own can stand in its
place (set the entry's `Material`).

### The Materials tab

The material workbench (R2b, docs/remaster-materials.md; phases 1 and 2): a
main screen **Materials** beside 2D, 3D, Script and Game (the plugin's main
screen, so the plugin is named "Materials" there). Textures come from the
Textures dock's **Materialize** (several selected), the part inspector's
**Materialize** beside each of the part's textures, or the tab's **+ Open
map**; a big lit preview with a compare wipe, one step per map (source,
albedo, height, normal, roughness, metallic, occlusion, emission: generate,
import a file, or skip), only the passes after a change made again: on the
GPU (phase 2: every map as shader passes, a change drawn in the next frame,
the occlusion settling over a few frames; the editor setting
`ffx_remaster/materials_gpu`, on by default) or, without a renderer, on the
CPU (256 px first, then the working size, off the main thread); the band
equalisers, colour samples (picked in the 2D view or on the object; their
masks isolated), the painted light's arrow, the colour check against the
classic texture. **Save** makes every map at the full size (on the GPU in
one frame) and writes the maps and the `ORMMaterial3D` into the files below
as one undo step (the height as a half-float `height.<hash>.exr`), the
settings as the entry's `PbrRecipe` (`Version` 3). Every setting change is
one step of the global undo history (a slider drag: one). The HDRIs of its
preview are four CC0 ones from Poly Haven in
`godot/addons/ffx_remaster/hdri/` (editor only, out of exports with the
plugin) and those of the folder in the editor setting
`ffx_remaster/hdri_dir`.

R2b's phase 4 (docs/remaster-materials.md, "Phase 4: what there is"): **+
map** at the end of the strip adds the other maps Godot's materials take
(bent normal, rim, clearcoat, anisotropy, subsurface, transmittance,
backlight, refraction, detail), each a step with its own settings, shown
live in the preview and written with the material's flags on Save (the
game's remastered path draws all but refraction); **Remove map** takes one
out. The Source step's **seam tool** (a height-aware overlap, or a scatter of
rotated copies) makes a texture that does not tile tile, every map
following it.

R2b's phase 5 (docs/remaster-materials.md, "Phase 5: what there is"):
**keys** while the tab has the focus (editor shortcuts, section "FFX
Materials": 0 to 9 the strip's steps, [ and ] step by step, the arrows the
queue, Tab the compare, Shift+Tab swaps the sides, Ctrl+S saves the texture,
R resets the setting under the mouse, F frames, M In map, ? lists them);
**colour picks** by a click or a drag in the 2D map or on the preview's
object, averaged over the pick radius; **Alt+click** on a surface in the 3D
view (or **Pick texture** in its toolbar) queues the texture under the
cursor (Shift: the part's every texture); the **In map** preview (the map
that uses the texture around it, lit by its remastered lighting, the live
material on the texture's surfaces, the classic look across the wipe); and
**ComfyUI** as the Source step's upscaler (progress, cancel, the answer
cached per texture and model, the tool, model and licence in the texture's
Source).

### Regions

R2b's phase 6 (docs/remaster-materials.md, "Phase 6: what there is"). Many
FFX textures are atlases: rock, grass and wood side by side. A texture can be
split into **regions**, each made with its own profile and settings, and
composited over the texture's own maps with soft borders. The **Regions** box
is at the top of the Source step:

- **From UV islands**: the parts of the map that draw the texture each cover
  some of it; their UV islands, grouped by the part that draws them (parts
  over the same pixels joined; a part that covers the whole texture, a
  repeating use, left out), become regions, largest first. From the open map
  when it draws the texture, else from the first map that uses it.
- **By colour**: pick a colour in the 2D map (**Pick**) or with the swatch,
  set how alike (lower: more pixels), **Add**: the pixels of that colour
  (the colour mask the samples use), specks and pinholes cleaned.
- **By hand**: **+ Region** (empty), then the tools under the list:
  rectangle (drag), polygon (click the corners; a double click, Enter or a
  right click closes it), **Brush** and **Erase** (Size in texture pixels;
  the middle button still pans). **Snap**: a shape or stroke takes the whole
  UV islands it touches.

The 2D map shows the regions in their colours (**Overlay**; the region being
edited stronger). Choosing a region in the list (or **Editing** atop every
step) makes the steps edit that region's settings: its profile (**Its
profile**) and every change as its own exception; the Source step's settings
(the upscale, sizes, tiling, the seam tool) and the material's own amounts
(parallax depth, emission energy, the added maps' amounts) stay the whole
texture's. **Feather** softens a region's border (classic texels); a later
region lies over an earlier one (▲ ▼ change the order). The preview (the
objects and In map) shows the composited material; the wipe compares it as
before. Every action is one undo step. **Save**, **Apply to queue** and
**Make again** write the regions on the texture's entry (`Regions`: each
region's name, colour, profile, its profile's hash, its exceptions, its
settings as made, its mask's file, feather, origin); the masks are small grey
PNGs in the texture's folder (`region-mask.<hash>.png`, named by their
content, gitignored with the maps). A region's changed profile marks the
texture as changed (Make again makes it with every region's profile as it is).

### AI estimation of maps

Optional, with a ComfyUI server and workflows of your own (R2b phase 6;
docs/remaster-materials.md, "AI estimation"). The albedo, height, normal and
roughness steps have a fourth mode, **AI**: the texture is sent to your server
(`ffx_remaster/comfyui_url`) with your workflow for that map, in the
background (progress, **Cancel**, **Run again**); meanwhile the step
generates as before. The answer is cached per texture, workflow and map
(`user://remaster/comfyui-cache/`, this machine's) and becomes the step's
input, its settings applied on top (the height's bands and contrast, the
normal's strength, bands and contrast, the roughness's samples, contrast and
bias, the albedo's colour match). Per map, three editor settings:
`ffx_remaster/ai_<map>_workflow` (an API workflow JSON: in ComfyUI,
Workflow, Export (API)), `ffx_remaster/ai_<map>_model` (the model's name, for
the record) and `ffx_remaster/ai_<map>_licence` (the licence of the model or
models it runs). **A workflow whose licence is empty is not run**: the step
says so. In the workflow, `%gdffx_input%` stands for the texture's image (a
LoadImage node's `image`), `%gdffx_output%` for the `filename_prefix` of the
SaveImage node whose image is the map, `%gdffx_map%` (optional) for the map's
name. gd-ffx ships no model and no third-party workflow, only a template of
placeholder nodes, `godot/addons/ffx_remaster/comfyui/example-map-workflow.json`
(and its README). Save and Apply to queue write the tool, workflow, model and
licence into the texture's `Source` (`material ai normal: normal estimated by
ComfyUI (workflow ..., model ...; licence: ...)`). Checked against the editor
check's fake server only.

### Profiles and the queue

R2b's phase 3 (docs/remaster-materials.md, "Profiles", "Phase 3: what there
is"). A **material profile** is every step's settings and the material's
under a name (`MaterialProfile`, a `PbrRecipe` with a title and a note),
`res://remaster/profiles/<name>.tres`: text, only the settings that differ
from the defaults, in a stable order; original work, so in git and in packs
(`res://mods/<pack>/remaster/profiles/`, the later pack winning per name, as
for textures). Eight starter profiles are committed: rock, stone and
concrete, sand and dirt, grass and foliage, wood, metal, fabric, plaster and
painted walls.

In the tab, under the queue: the profile picker (loading a profile sets the
queue's settings: one undo step), **Save** (over the profile), **Save
as...**, **Revert** (the queue back to the profile), **Rename...** (the file
and every texture made with it), **Delete**; each one undo step that swaps
the file's text. A setting changed while a texture is current can be
**pinned** to it (○ beside the setting; ● when pinned): an exception, made
with its own value, the queue's going back to what it was before; **Clear**
unpins all. **Apply to queue** makes every queued texture with the queue's
settings and its own exceptions at the full size in the background (the GPU
when there is one, one texture a frame or two; else the CPU on a worker), a
progress bar, **Cancel** (what was made is kept), each result's albedo in
the queue and the strip as it comes, the entries set as one undo step at the
end (files named by content: undo and redo overwrite none). A texture's
entry keeps its profile, the profile's hash and the settings that differ
from it; when a profile changes, the queue and the Textures dock mark its
textures ("Rock changed: 37 textures") and **Make again** makes them with
the profile as it is now and their own exceptions. R2's batch (a review list
of up to 48) is gone: the queue and a profile do its job.

### Upscaling

Built in: Lanczos-3 (`Lanczos`), any factor, colour in linear light and
premultiplied by alpha. With a ComfyUI server of your own (off by default:
set the editor setting `ffx_remaster/comfyui_url`, e.g.
`http://127.0.0.1:8188`, and `ffx_remaster/comfyui_upscale_model`, default
`RealESRGAN_x4plus.pth`) **Upscale** sends the texture there instead
(`ComfyUpscaler`: core nodes only, LoadImage, UpscaleModelLoader,
ImageUpscaleWithModel, ImageScaleBy, SaveImage) and makes the answer the
replacement. gd-ffx ships no model and downloads nothing; use models whose
licences allow it and write the model and its licence into the texture's
`Source` (the editor setting `ffx_remaster/comfyui_model_licence`: the
Materials tab writes it there itself). The Materials tab's Source step takes
ComfyUI as its upscaler too (R2b phase 5: in the background, with progress
and cancel, the answer cached in `user://remaster/comfyui-cache/`). The
client is checked against the editor check's fake server (the same calls);
it has not been run against a real ComfyUI yet.

## The render path

Each map draws **classic** (the game's own shaders, pixel for pixel the
classic look) or **remastered**, as its overrides' `RenderPath` says (the
dock's **Render**):

- its parts draw under Godot's lit pipeline (`RemasterLook`, `MapLook`): the
  texture's PBR material, or its colour texture (the replacement or the
  classic one) as a plain rough surface. The lit material keeps what the
  classic one does the game's way: blend, cull and depth modes, the alpha
  test, the UV set, the scrolling UVs (clouds), the morphs the game drives.
  The game's vertex colours hold baked light and tint (128 neutral): the
  map's `VertexLight` ("Lighting") says how much of it stays under the real
  lights (all of it by default);
- the sky keeps the classic look: sky domes (`m_DObjKind` 1) and, after
  ffx-remix's R-45 rule, the parts grouped by texture that reach 1,000 units
  from the vertical axis, centred on it and rising above the ground (Besaid's
  four scrolling cloud quarters), unless the map's lighting hides them for a
  sky of Godot's; water, 2D layers and characters keep their classic
  shaders, and so do the map's blended parts (the game's painted layers: wet
  sand, shallow water, shading) unless the lighting says `BlendedParts = Lit`;
- the map's lighting ("Lighting"): the game's key light as the sun, the
  ambient, the sky, fog, tone map, GI. Under Forward+ `ClassicPost` is off and
  the classic shaders still drawing write their colours as linear ones
  (`ffx_linear_out`), so Godot's encode gives them back (docs/architecture.md,
  "Colours"); premultiplied ones (the refraction sea, `ONE, INV_SRC_ALPHA`
  map parts) decode their colour before the alpha, so the sea keeps its
  colour. Under Compatibility they write them as they are, and the lit
  shaders take the game's colours as they are too (`ffx_lit_gamma`), with
  their own exposure ("Lighting quality").

To compare: the 3D view's **Remastered** toggle (the editor's 3D toolbar; a
map opens as its render path says), F7 in the game (classic and remastered
in turn, every map, for the run), the options' **Render path** (as each map
says, classic everywhere, remastered everywhere; saved), or
`--render-path=map|classic|remastered`.

## Lighting

A remastered map's lighting and atmosphere (R3) is a Godot resource beside
its overrides, `res://remaster/maps/<level>/lighting.tres` (a `MapLighting`,
`godot/src/Remaster/MapLighting.cs`; a pack's
`res://mods/<pack>/remaster/maps/<level>/lighting.tres`). Unlike the
overrides it layers per file: the last layer that has one wins, whole
(`LightingLayers`: the project's, the packs' in load order, the registered
ones; `--no-overrides` turns them off). A map without one draws with the
defaults, which follow the game's look (`Look`: the key light, the ambient,
the background and fog colours, the fog's range). Only the remastered path
reads it; a classic map never does.

| Group | Fields | Default |
|---|---|---|
| Key light | `KeyMode` (Game: the game's direction, `Look.lights[0]` turned as the characters' shader turns it; Custom: `KeyDirection`; Off), `KeyColourMode` (Game: the game's colour kept by `KeyGameTint`, warmed by `KeyWarmth`; Custom: `KeyColour`), `KeyEnergy` (times the game's level against Besaid's daylight one), `KeyShadows`, `KeyAngularSize` | Game, Game, tint 0.2, warmth 0.35, 2.0, shadows, 0° (above 0: soft shadows by Godot's PCSS, costly) |
| Ambient | `AmbientMode` (Game: the map's and characters' ambient, its hue kept by `AmbientGameTint`; Custom; Off), `AmbientColour`, `AmbientEnergy` (times the game's level), `AmbientSkyContribution`; `VertexLight` (the game's baked vertex light kept, 0..1), `BlendedParts` (Classic, Lit) | Game, tint 0, 0.65, 0; vertex light 1, blended classic |
| Fog | `FogMode` (Game: the game's colour, near, far, curve and limit as Godot's depth fog; Custom; Off), `FogColour`, `FogDensity`, `FogBegin`, `FogEnd`, `FogCurve` | Game |
| Volumetric fog | `VolumetricFog`, `VolumetricDensity`, `VolumetricAlbedo`, `VolumetricEmission`, `VolumetricAnisotropy`, `VolumetricLength` | off |
| Sky | `SkyMode` (Game: a procedural sky in the game's background and fog colours; Procedural: `SkyTop`, `SkyHorizon`, `SkyGround`; Physical; Panorama: `SkyPanorama`, an HDRI of the project's), `SkyEnergy`, `SkyHideClassic` (the classic sky parts hidden) | Game, the classic sky parts kept |
| Tone and colour | `ToneMapper` (Linear, Reinhard, Filmic, ACES, AgX), `ToneExposure`, `ToneWhite`, `ToneSaturation`, `ToneContrast` | Filmic, 1, 2.5, 1.15, 1 |
| Glow | `Glow`, `GlowIntensity`, `GlowThreshold`, `GlowBloom` | on, 0.5, 1, 0 |
| Screen-space | `ScreenSsao` (+ radius, intensity), `ScreenSsil` (+ intensity), `ScreenSsr` | all off |
| Global illumination | `GiMode` (None, Sdfgi, VoxelGi, LightmapGi), `GiEnergy`, `GiSize`/`GiCentre` (the VoxelGI's box; zero: the map's middle, at most 1,200 x 400 x 1,200), `GiCascades`, `GiCellSize` (SDFGI) | None |
| Volumes | `Volumes` (`LightingVolume`s: "Volumes") | none |

The defaults are measured, not guessed ("The colour check"): the game's key
light and ambient are made for its characters and lean blue (Besaid's key
light 1.20, 1.29, 1.37; ambient 0.34, 0.34, 0.45), so they give their
direction and level and little of their hue; the baked vertex light stays
whole (it holds the map's colour: without it Besaid's sand lost its yellow);
Filmic with a little saturation keeps the classic chroma better than AgX or
ACES did there.

### In the editor

Opening a map builds its lighting as nodes under `Lighting`, each tagged
with its stable ID (the `<resource>#<fragment>` form of the part IDs,
docs/importer.md):

```
bsil00                    MapEditRoot
├── ClassicMap ...
├── Additions
└── Lighting
    ├── KeyLight          x:level/bsil00#light/key     a DirectionalLight3D: the gizmo turns it, the inspector colours and dims it
    ├── Ambient           x:level/bsil00#ambient       the Ambient fields, VertexLight, BlendedParts
    ├── Fog               x:level/bsil00#fog           the Fog and Volumetric fields
    ├── Environment       x:level/bsil00#environment   Sky, Tone, Glow, Screen, GI
    └── Volumes
        └── <name>        x:level/bsil00#volume/<name> a box (VolumeNode)
```

`Ambient`, `Fog` and `Environment` (`LightingFacet`) show their part of the
map's `MapLighting` in the inspector as their own properties: Godot's
inspector edits them, each change one undo step, the revert arrow goes back
to the default. The key light is shown where the game puts it: the editor
runs no game, so the game writes each map's look when it shows the map
(`user://remaster/looks/<level>.json`, `LookCache`: the key light, the
ambient, the background and the fog; never in git); a map the game has not
shown here yet gets the defaults' direction (the log says so: play it once).
With the 3D view's **Remastered** toggle on, the game's own rig previews the
lighting (the key light node is its sun; the volumes by the editor camera's
place).

Saving (**Save edits**, Ctrl+S) writes `lighting.tres` beside the overrides:
the key light compared with the game's (a turn makes `KeyMode = Custom` and
the direction, a colour `KeyColourMode = Custom`, an energy the file's
`KeyEnergy`; hidden or deleted: `Off`), the facets' fields as edited, the
volume nodes. Text, only the fields that differ from the defaults, stable
sub-resource IDs (`v_<hash of the volume's name>`): a save without an edit
changes no byte, and a lighting that says only the defaults has no file.

### Volumes

A volume (`LightingVolume`) is a box with lighting of its own: **Add volume**
in the dock (one undo step; around the selected part, else in front of the
view), moved, turned and scaled with Godot's gizmos (the scale goes into its
`Size` on save), its values in the inspector. Each value has a switch
(`SetExposure`, `SetAmbient`, `SetFog`, `SetKeyEnergy`, `SetSaturation`,
`SetVolumetricDensity`): a volume says only what it changes. In the game the
camera's place weighs each volume (0 outside, 1 deeper than `Blend` units
inside every face, smooth between), the set values blended over the map's
in the list's order (a later volume over an earlier one).

### Lights and probes

**Add at selection** adds, besides the shapes: omni, spot and directional
lights (shadows on), a reflection probe (a box, box projection, updated
once) and Godot's fog volume (local volumetric fog; it needs the map's
volumetric fog on). They are additions (docs/remake.md, "Overrides"): saved
in `additions.tscn`, anchored to a part or the map, in the game on every
path, but they only light the remastered path (the classic shaders are
unshaded).

### Global illumination and baking

- **SDFGI**: real time, Forward+ at the high quality only.
- **VoxelGI**: a VoxelGI over `GiSize` (or the map's middle), baked on the
  player's machine the first time the map shows remastered (Besaid beach:
  0.3 s, 131 surfaces, on the development machine's CPU renderer) and kept
  in `user://remaster/bake/<level>/voxelgi.res`. The bake is made from the
  player's game content, so it is never shipped and never in git: the
  lighting file holds the recipe (the mode, the box, the energy). The bake
  sees each lit surface's albedo texture (Godot's voxelizer reads only its
  own materials: plain ones stand in for the bake). A changed box bakes
  again.
- **LightmapGI**: not yet (the maps have no lightmap UVs; drawn as None).

### Lighting quality

The options' **Lighting quality** (`video/lighting`, `--lighting=`) caps
what a remastered map's lighting costs:

| | high | medium | low (Steam Deck) |
|---|---|---|---|
| GI | the map's | VoxelGI only | none |
| volumetric fog | the map's (128 x 96 froxels) | the map's (64 x 48) | off |
| SSAO / SSIL / SSR | the map's | the map's SSAO | none |
| shadows | 4 splits, the project's atlas and filter, 3,000 units | 2 splits, 2048, 2,000 | 2 splits, 2048, 1,200 |

The Deck preset sets low. Compatibility draws no GI, volumetric fog or
screen-space effects whatever the quality; its lit shaders light the game's
colours in gamma, so the rig gives it its own scales (key light x0.55,
ambient x1.25, exposure x0.8: measured by the colour check, not over-bright).
Budgets for the Deck (to be measured on the device: R8): low quality, the
3D at 75 %, 60 fps; one directional shadow, no GI.

### The colour check

A remastered map must stay recognisably FFX: its colours keep the classic
map's hue and saturation balance, with better light. `ColourBalance`
(`godot/src/Remaster/ColourBalance.cs`) and `tools/compare/colour.py`
measure a remastered picture against the classic one of the same view: the
mean OKLab lightness, chroma and hue over the picture, and over a 4 x 3
grid. The tolerances:

| | limit |
|---|---|
| mean lightness, remastered minus classic | ±0.06 |
| mean chroma, remastered over classic | 0.75 .. 1.35 |
| chroma-weighted mean hue | 12° |
| the cells' median lightness difference | 0.15 |
| the cells' median hue difference (cells with colour) | 25° |

`--selftest-lighting` (with a renderer) and `tools/compare-shot-win.sh`
(`FFX_SHOT_TEST="--selftest-lighting ..."`) apply them; R2's rig failed them
everywhere (Besaid, lavapipe Forward+: lightness -0.21, chroma x0.35, hue
off by 159°: grey sand, a black shade). The causes: Godot takes light,
ambient and fog colours as sRGB and decodes them, so R2's sun was decoded
twice (blue) and its ambient became 0.09 (black, blue); the game's blue key
light and ambient multiplied the yellow sand to grey; half the baked vertex
light was dropped; AgX desaturated; and the refraction sea lost its colour
under the linear output (a premultiplied colour decoded with its alpha).

Measured on Besaid beach (developer save 7, the defaults, 2026-11-17):

| Renderer | lit from the colour textures | with PBR materials |
|---|---|---|
| Forward+, Vulkan on lavapipe (Xvfb) | lightness +0.007, chroma x0.88, hue 2.0°; cells 0.022, 1.7° | +0.003, x0.87, 2.1°; cells 0.034, 2.9° |
| Compatibility (Xvfb) | +0.040, x0.93, 3.5°; cells 0.040, 2.1° | within the limits |
| Forward+, D3D12 and Vulkan (Intel Arc 140T, the Windows export) | +0.006, x0.89, 2.0°; cells 0.022, 1.7° | +0.000, x0.87, 2.1°; cells 0.034, 3.0° |

The GPU check (`tools/compare-shot-win.sh` with `--selftest-lighting`): the
classic picture scores 8.9 against the original's, as on main; the whole check
passes on D3D12 and on Vulkan (the defaults, PBR materials, the lighting file
with volumetric fog, VoxelGI and a volume, the three qualities, F7).

**D3D12 and Vulkan.** Under D3D12 the remastered view lost the device
(`0x887a0005`, device removed) in six of seven runs on the Windows export with
an Intel Arc 140T (2026-11-18), at random moments after the switch (with SSAO
or without, soft shadows or not, the lighting file or the defaults); the
classic view never did. Under Vulkan, on the same GPU, four of four runs
passed with no error. No GPU reset showed in Windows' logs (the device was
lost inside the process only). gd-ffx renders with Vulkan on Windows:
Godot 4.7's default for `rendering/rendering_device/driver.windows` (the
project sets nothing, so the editor's saves keep `project.godot` unchanged;
Godot falls back to D3D12 where Vulkan is missing; `--rendering-driver d3d12`
picks D3D12). Only `tools/compare-shot-win.sh` used D3D12 by default; it uses
Vulkan now. The cause is not
known: the D3D12 debug layer (Windows' optional "Graphics Tools") was not
installed. Check D3D12 again after a Godot or driver upgrade
(`tools/compare-shot-win.sh - OUT 7 d3d12` with
`FFX_SHOT_TEST="--selftest-lighting --selftest-timeout=400"`, a few runs).
A remastered picture identical to the classic one (a
frame not drawn, as a lost device leaves) fails the colour check.

## The game's data

The game's logic stays the game's (docs/remaster-plan.md, "Rules that stay"):
characters walk on its walk mesh, its triggers start its events, its cameras
frame the rooms. Opening a map shows that data as read-only nodes under
`GameData`, so remastered geometry can be made to line up with it (R5):

```
bsil00                    MapEditRoot
├── ClassicMap ...
├── Additions, Lighting
└── GameData              the game's data (the editor's: locked, never saved)
    ├── WalkMesh          Page0 (shown), Page1... (the pages the scripts switch to), each with its Edges
    ├── Triggers          bsil0000_w32 ... a line trigger (a wall along the line) or box; labelled "to <place> (<key>, map 0x..)" when it changes the map
    ├── Entrances         bsil0000_e0 ... an arrow where the player arrives by entrance N, facing as the game turns them
    ├── Actors            bsil0000_w3_n172 ... where an actor's script places it at the start (model, facing)
    ├── Cameras           Scene0... (a 2D scene's fixed camera), Game (the game's camera as it last showed the map)
    ├── Characters        Player_c001, Slot3_n172 ... where the game had them (the capture)
    └── Alignment         Flagged: the triangles the alignment check flags
```

- **Walk mesh**: the triangles the characters stand on (`MAP1` sections 2 to
  8, pages 0 to 6 of `yiSetIDPage`; docs/importer.md, "The game's data"),
  translucent with their edges, coloured by each triangle's kind (bits 0-6 of
  its map id word, `Sg_IdMatchChr`): green open, red closed to everyone,
  orange closed to the player, yellow closed to all but the player or to
  chocobos, blue switched by the scripts (`0x30..0x3f`), grey other kinds
  (they block no one). Besaid beach: one page, 839 triangles, all open; the
  map starts on page 0, the others are shown hidden.
- **Triggers**: the event scripts' trigger workers as their init entries set
  them with constant arguments: line triggers (type 2, yellow) and line
  sensors (5, cyan) as a wall 16 units high along the line (extended as the
  game extends it), box triggers (3, magenta) and box sensors (6) as boxes
  turned by their yaw (30 units high when the box has any height). One that
  changes the map is orange and labelled with where it leads, by the
  catalogue (Besaid beach's exit: "to Besaid - Crossroads (bsil02, map
  0x14)"); the inspector's **Open target** opens that map.
- **Entrances**: where the player arrives by each entrance of each event of
  the level (the script's table: position, facing, camera entry); **Actors**:
  the type-1 workers' start places (`setPosition`, `SetModel`, `SetYaw` in
  their scripts), which events change later.
- **Cameras**: a 2D-layer scene's fixed camera (the `YNSC` block: eye,
  target, up, field of view; the game's camera on developer saves 12 and 14
  is the file's to 0.01 units and 0.02°) and the game's camera as it last
  showed the map (the capture). Both are Godot cameras: select one and tick
  the 3D view's **Preview**, or the inspector's **View from it**. 3D maps'
  follow cameras (the scripts' camera worker) are not read yet.
- Click any of them: the inspector's **FFX game data** section says what it
  is (worker, type, coordinates, where it leads, the kinds of a walk mesh).
  The dock's **Game data** box toggles each group (also the eyes in the
  scene tree) and says what the map has.

### The capture

The editor runs no game, so the game writes what only it knows a few seconds
after it shows a map (once a run; like R3's looks): each placed part's world
(the parts the scripts move: Besaid's birds), the display objects' morph
values, the camera, the characters. `user://remaster/captures/<level>.json`
(`MapCapture`; a check writes it only into `--remaster-captures=DIR`), on the
player's machine, never in git (it is read from the running game). Opening
the map then puts those parts where the game had them (their classic base
moves there too, so they show no edit and a move is an offset on the game's
placement, as the game applies it), sets their morphs, and shows the game's
camera and characters. A map the game has not shown here keeps the file's
places ("no capture" in the dock).

### The alignment check

**Check alignment** in the dock: where the open map's remade geometry (every
added node's meshes, every replacement) disagrees with the walk mesh. Each
walkable triangle is sampled every 6 units; along the vertical through a
sample:

| Result | When |
|---|---|
| **Blocked** (magenta) | a remade surface over the walk mesh within the clearance (12 units: the characters stand in it or walk through it) |
| **Floating** (yellow) | else the nearest remade surface under it is lower than the tolerance (2 units: a gap under their feet) |
| ok | a remade surface within the tolerance and nothing in the way, or only a roof far over it |

Samples no remade surface covers are not judged (the classic map is there).
The flagged triangles are marked under `GameData/Alignment`, the dock lists
them (kind, by how much, the node), a click puts the view over one.
**Check scene...** checks a remade map's scene (render space, at the map's
root) the same way without keeping it. A node with the metadata
`ffx_alignment_skip` is left out (a sky dome); lights, probes, cameras and
fog volumes always are. The game's check runs it too (`--selftest-alignment`,
"Checks").

## Characters

The **Characters** dock (beside **Remaster** and **Textures**; R6) works on the
game's characters by their model id (`c001`), the id the game's `Actor.model`
names and the remake registry keys (docs/remake.md, "Retargeting").

- **The list**: every character model of the player's archive (872 in the
  HD archive: `c` the party and other people of `chr/pc`, `m` monsters, `n`
  people, `s` aeons, `w` weapons, `f` objects, `k` skeletons), with the game's
  name where the kernel gives one (the party's from `ply_save`, a monster's
  from the monster table record of its number; 348 named), its kind, whether
  it has motions of its own, and its remake (status, `rig`). **Search**
  matches the id and the names in every language; the kind box and
  **Remade** filter it. From `FfxGame.classic_characters` (`ffx-import
  characters`).
- **Open** (or a double click) writes `res://remaster/edit/chr/<id>.tscn` (the
  root and the id only; gitignored, not exported) and opens it. Its root
  (`CharacterEditRoot`) builds, in the game's character space (half a turn
  about z, as the game places characters): the classic import *with its
  motions* (the `chr-motions` import: the model and every clip of its own
  motion groups as glTF animations, named `<group>/<clip> (<motions>)`, the
  motion numbers that play the clip; 140 for Tidus, 22 MB, editor only), and
  the remade scene when one is assigned, 14 units beside it, driven each
  frame by the classic skeleton's animated pose through the same retargeter
  as in the game.
- **Motions**: the motion box plays one (looping), **Pause**/**Play**, the
  slider scrubs it.
- **Assign model...**: a `.tscn`, `.scn` or `.glb` (any skeleton) becomes the
  open character's remake: its rig is made automatically (below) and the
  scene `<id>.tscn` (a `RemadeCharacter` root over an instance of your scene)
  and its rig `<id>.rig.tres` are written into `res://remake/chr/`, where the
  remake convention finds them (a pack ships them at the same paths, or under
  `res://mods/<pack>/` with a `remake.json` entry). One undo step (the files as
  they were).
- **Bones**: the mapping, one row per remade bone: the game's element
  (`SkeletonNode_i`), the body part it was found as (`hips`, `upperarm.L`,
  `spine1`...; "by hand"), where its position comes from (`Rotation`: the
  remade bone's own proportions; `Game`: on the game's joint; the hips take the
  game's). **Auto map** maps again (rows set by hand stay), **Auto align**
  turns and scales the model to the game's (up, facing, head height, feet);
  **Set element** maps the selected bone by hand (-1: none). Each is one undo
  step, and the rest offsets are made again after it.
- The cloth chains (each `Game` or `Godot`, with the game's cloth segment it
  follows), the face table and the attachments are listed under the bones;
  status and notes are set below. **Save** (or Ctrl+S on the scene) writes the
  rig.

The automatic rig (`RigBuilder`):

- **Body parts**, on both skeletons: by name patterns first (`hips`/`pelvis`,
  `spine*`, `chest`, `neck`, `head`, `jaw`, `clavicle`/`shoulder`,
  `upperarm`/`arm`, `forearm`/`lowerarm`, `hand`, `thigh`/`upleg`,
  `shin`/`calf`/`leg`, `foot`, `toe`, each with its side: `.L`, `_R`,
  `Left...`, `mixamorig:` prefixes dropped) when they give the hips, the head,
  both upper arms and both thighs; else by the hierarchy and rest positions:
  the feet are the lowest leaves (the second at the first one's height), the
  hands the widest pair of leaves above the hips outside the legs, the head
  the highest leaf under the chest between them; each limb's joints are its
  bones grouped by place (the deepest of each group drives it), its upper and
  lower parts the longest consecutive pair. Leaves at the skeleton's origin
  or on their parent are left out (the classic models' unused face bones).
  The game's skeleton has no names: it is labelled this way, with its
  attachment points as hints (9 the head, 13 the jaw). For Tidus: hips 1,
  spine 3, 6, 7, neck 60, head 61, jaw 63, clavicles 9 and 35, upper arms 12
  and 38, forearms 13 and 39, hands 16 and 42, thighs 78 and 91, shins 79 and
  92, feet 82 and 95.
- **Mapping**: part to part; the spines by order (first to first, last to
  last), every neck bone to the game's neck, the jaw to point 13's element.
- **Alignment**: the remade up and facing turned onto the game's (y down,
  facing -z), the head height scaled to the game's, the feet put on the
  game's (`CharacterRig.Align`).
- **Rest offsets**: each mapped bone with one branch of mapped bones under it
  is turned, at the rest, so the next mapped bone (at a place of its own in
  the game) lies where the game's next element does (a T pose meets the
  game's A pose); its rotation is then taken relative to its element's rest
  (`RigBone.Rest`).
- **Cloth**: bones named as cloth (hair, tail, hood, cape, skirt...) and not
  mapped make chains; a chain whose bones each have a classic cloth vertex of
  one segment within 12 % of the body follows the game's cloth (each bone a
  vertex, half a bone's length apart at least), else Godot's spring bones.
- **Face**: the game's jaw onto a blend shape named as a mouth opening
  (`mouth_open`, `jaw_open`...), from the jaw's rest angle to 7° more.
- **Attachments**: the party's hand points (4 and 5, where the game binds
  weapons) onto a remade bone named as a weapon socket on that side, else the
  remade hand; the side is the mapped hand above the point in the PS2
  skeleton (`FfxGame.classic_character_skeleton`: the points are ends the HD
  model has no node for), whose bind pose also gives the point's rest frame.
  Elements without an attachment carry what the game binds to them on the
  remade bone mapped to them.

## Effects, audio and movies

R7a (docs/remaster-plan.md, "R7a in detail"). The **Media** dock has three tabs;
every change writes its file at once and is one step of the editor's undo
history (the file's text before and after).

### Effects

The game identifies its effects by who makes their draws (docs/remake.md,
"Effects"): a map's field program (`bsil00-fp15`: Besaid beach's save sphere
glow, entry 15 of the map's effect table) or a spell overlay (`magic-004c`).
Each effect the game draws is recorded once a run, like R5's captures, into
`user://remaster/effects/` (`--remaster-effects=DIR`; a check writes only
there): `<id>.fxrec`, its draw list over its first 120 updates (4 s; at most
400,000 vertices), positions relative to its place, with the textures' names
and the look each draw has, and `index.json` (what it is, the map, frames,
textures, place, when). The recordings hold the game's geometry: they stay on
the player's machine, never in git.

- **The list**: the recorded effects (id, what, frames) and the remade ones
  the game has not recorded yet, with the remake's file.
- **Open**: the edit scene `res://remaster/edit/effect-<id>.tscn` (not in git)
  replays the recording under `Classic`, drawn as the game draws it (the
  effect shaders of `EffectDrawView`, the textures from the archive), and
  instances the remake under `Remake`, beside it (**Side by side**) or at the
  same place. **Pause**/**Play** and the slider scrub the classic replay (30
  frames a second); the remake runs on its own (Godot previews particles in
  the editor).
- **Assign scene...**: a scene of yours (particles, meshes, lights; any
  `Node3D`) becomes the effect's remake: the convention's file
  `res://remake/effect/<id>.tscn` is written, a `RemadeEffect` root with your
  scene instanced under it (its options: `HideClassic`, `Follow`, `Offset`,
  `Linger`, `Note`; docs/remake.md, "Effects"). **Classic** removes it.

### Audio

The game's sounds by the ids it plays them by, read from the player's copy
(`FfxGame.classic_sounds`, `ffx-import`'s `sounds`):

- **Music**: the 92 ids the game maps to tracks, named (`10`:
  `1_09_battle_theme`);
- **Sound effects**: 6,774 sequence numbers (US banks), each with its sample
  (`c355`: `50005`, Besaid beach's sea);
- **Voice**: 8,665 voice lines named by their events (`voiceidmapper.txt`).

Search by id (hex) or name. **Play classic** decodes the game's sample (its
loop points too); **Play replacement** plays yours. **Assign file...** replaces
the selected sound by an Ogg Vorbis, WAV or MP3 file (any path: keep it in your
project or pack) with **Loop**, **from**/**to** (seconds; 0: the file's end) and
a volume in dB, into `res://remaster/audio/audio.tres` (a `RemasterSoundSet`);
**Classic** gives the id back to the game.

### Movies

The game's movies by name (`LCI`, with the languages it has: `LCI_jp`,
`LCI_us`; the texture videos `*_480` are left out). **Assign video...**
replaces a movie (every language's; an entry named `LCI_us` replaces one
language only) by an Ogg Theora `.ogv` with its sound (**Sound**: the game's,
the video's own, a file, none) into `res://remaster/movies/movies.tres` (a
`RemasterMovieSet`; subtitles: the entry's `Subtitles`, a SubRip `.srt`, in the
inspector). **Preview** plays the replacement in the dock. gd-ffx does not
draw the game's own movie pictures yet, so there is no classic preview.

### Files and layers

| What | The project's | A pack's | Run time (checks) |
|---|---|---|---|
| remade effects | `res://remake/effect/<id>.tscn` (or a `remake.json` entry, kind `effect`) | the same paths in the pack, or `res://mods/<pack>/remake.json` | `RemakeRegistry.Register`, `--effect-scene=FILE --effect-id=ID` |
| sounds | `res://remaster/audio/audio.tres` | `res://mods/<pack>/remaster/audio/audio.tres` | `--audio-file=FILE` |
| movies | `res://remaster/movies/movies.tres` | `res://mods/<pack>/remaster/movies/movies.tres` | `--movies-file=FILE` |

A later layer wins per id (`Classic` on an entry pins the id to the game's).
In the game, **F8** turns every replacement off and on for the run (remade
effects, sounds and movies; `--no-replacements` starts with them off), as F7
does the render path.

## The remaster wizard

R4 (docs/remaster-plan.md, "R4 in detail"). The **Wizard** dock makes a first
automated pass over maps and stages it for review; nothing reaches the project
until a map is accepted.

- **Run** over the map selected in the Remaster dock, its area, the maps the
  project changes, or all maps. For each map the wizard loads its classic import
  (no scene is opened), lists its colour textures with how the map draws them,
  chooses a **profile per texture** (rock, stone and concrete, sand and dirt, grass
  and foliage, wood, metal, fabric, plaster and painted walls, or keep classic:
  sky, water, glows and effects, background paintings) with a score and its
  reasons, makes the materials with those profiles (R2b's run: the GPU when the
  Materials tab uses it, else the CPU), stages the lighting (R3's defaults from
  the game's look, VoxelGI over the map's middle; not SDFGI, which leaked light on
  the GPU), and, with a renderer, renders the before/after pictures, matches the
  exposure to the classic picture once and runs the colour check.
- **Pause** stops after the map being made, **Resume** goes on (in the next
  editor session too), **Cancel** drops the map being made. 2D-layer maps are
  skipped (or noted as kept classic: the box under the buttons). The progress bar,
  the status and a summary (maps done, to review, accepted, skipped, failed;
  materials per profile; the time per map) are at the top, the maps of the batch
  below with their state, time and colour check.
- **The review** of the selected map: the classic and the remastered picture side
  by side (the game's camera when the game captured the map, else three views
  over the walk mesh; **View** steps through them), the colour check (orange when
  it fails), the lighting (GI, exposure), and per texture its made albedo and
  normal, the profile with its score and lead over the runner-up, and the
  decision. A texture's reasons show under the list ("sand 3.7 (on the ground
  +1.0, common in bsil +0.4, warm share 0.995 (its 0.98) +0.2, ...); then wood
  1.5"). **Accept** keeps its material, **Reject (classic)** gives it none,
  **Make with** makes it again with the chosen profile; **Pictures again** draws
  the remastered pictures with the textures as decided now.
- **Accept map** applies the pass as one undo step: the materials (not the
  rejected ones) as texture entries (with the profile, its hash, the settings that
  differ from it), the render path remastered, the status in progress (a higher
  one set by hand stays), the pass's note added to the map's notes ("wizard pass
  2026-11-18: 50 material(s) (grass-and-foliage 20, sand-and-dirt 14, ...), 3 kept
  classic; lighting from the game's look, GI VoxelGi, exposure 0.8"), and
  `lighting.tres` (only what differs from the defaults). An open map is opened
  again. **Discard** drops the pass. Each review action is one undo step of the
  global history; making a pass is none (no project file changes).
- **Headless** (the editor without a window): `godot --headless --editor --path godot
  -- --remaster-wizard --maps=bsil00,slik08,ptkl14` makes the batch and prints
  `ffx wizard: DONE <summary>`; `--wizard-resume` resumes a paused batch,
  `--wizard-pause-after=N` pauses after N maps (and quits), `--wizard-accept`
  accepts every staged map, `--wizard-2d-classic` notes 2D-layer maps as kept
  classic. Headless there are no pictures and no colour check.

The files: the wizard's state is `res://remaster/wizard/state.json`
(`--remaster-wizard-dir=DIR`): the batch, and per map the staged pass (texture IDs,
kinds, profiles, scores, reasons, decisions, the staged entries, the lighting's
changed fields, the colour check), text; gitignored in gd-ffx's checkout. The
made maps go to the texture folders as R2b's runs write them (content-named; a
staged map's are unreferenced until accepted, and stay when a pass is discarded).
The pictures and thumbnails are the player's game: `user://remaster/wizard/<map>/`
(a check's: `DIR/pictures/`), never in git. A map the game has not shown on this
machine has no look cached: its lighting follows the defaults and the review says
"play the map once".

## Your work and git

Replacements and generated maps are your own work products, but the ones
made from the game's textures (upscaled, de-lit, generated) derive from the
player's copy: they never go into gd-ffx's git. `res://remaster/textures/`
is gitignored in gd-ffx and left out of its exports; gd-ffx's examples are
synthetic (the checks make their test images at run time and remove them).
A remaster project is a pack of its own (docs/remake.md, "Making a pack"):
move the folder to `res://mods/<pack>/remaster/textures/` (its paths are
relative) and export it with the pack's other files. Whether you may
publish such a pack depends on what it holds; textures made by hand from
nothing of the game's are yours.

A character's rig (`<id>.rig.tres`) holds names, element numbers and numbers
(the alignment, the rest offsets: turns between your bones and the game's
rests, the cloth vertices' numbers and their rest directions): your work, fine
in a pack. The classic import with its motions (`chr-motions`) stays in the
classic cache on the player's machine.

Remade effects, sounds and movies are your own files; the editor's sets hold
ids and paths. What is made from the game stays on the player's machine: the
effect recordings (`user://remaster/effects/`) and the classic sounds the dock
decodes (in memory only).

The game's data in the editor is read from the player's archive each time a
map opens and never stored; the captures (`user://remaster/captures/`) stay on
the player's machine like the looks.

A map's `lighting.tres` holds IDs and values only (directions, colours,
energies, the volumes' boxes, an HDRI path of the project's): original work,
fine in a pack and in git. What is made from the game stays on the player's
machine: the looks the game writes for the editor (`user://remaster/looks/`)
and the GI bakes (`user://remaster/bake/`).

## Checks

```bash
FFX_GAME_DIR=<game> tools/remaster-editor-check.sh [OUT_DIR]                    # headless
FFX_EDITOR_SHOT=1 FFX_GAME_DIR=<game> tools/remaster-editor-check.sh [OUT_DIR]  # under Xvfb, with pictures
```

0. R5, before the editor: the game's `--selftest-alignment --devsave=7
   --remaster-captures=DIR/captures` (below: "The alignment check in the
   game") and the map check on developer save 14 (`--selftest-map
   --devsave=14`) leave their captures of Besaid beach and Kilika's inn in
   `DIR/captures`, which every editor run below reads.
1. The editor (`--editor -- --remaster-editor-test --remaster-maps=DIR`;
   the overrides go to DIR, never the project's) drives the plugin through
   the dock's and the inspector's code: the archive and the catalogue
   (counts, search by name, event and in Japanese, the changed filter),
   Besaid beach opened (every part a node by ID, the segments apart, the
   classic materials), then a move (an undoable transform change, as a
   gizmo makes), a hidden segment, a removed instance, a part replaced by a
   scene and a box added at a part, each one undo step, undo and redo; the
   scene saved (no classic content in its file), the overrides written and
   read back, a second save byte for byte the same; the map opened again
   with the same edits; the status set, undone, set again; the play plan.
   It writes `DIR/expect.json`. With `--editor-shot=FILE.png` under a
   renderer: the 3D view, the whole window, the window with the dock.
2. The game (`--selftest-saved-overrides --overrides-file=DIR/bsil00/overrides.tres
   --overrides-expect=DIR/expect.json --devsave=7`) registers the editor's
   file as a layer, boots on Besaid beach and checks each edit in the
   running scene against what the editor wrote: removed, hidden, moved by
   the saved offset on the game's placement, replaced, the box where the
   editor showed it (within 0.05 units), the catalogue's status.
   Then R2 (with `--remaster-textures=res://remaster/check/textures`, in the
   project so Godot imports the PNGs; gitignored, removed at the end): the
   where-used index (Besaid's textures, the cache), the open map's most drawn
   texture replaced by a generated test image (a checker) and its surfaces
   drawing it, one undo step, undone and redone; Besaid's render path set to
   remastered (written), the map drawn remastered, the view's toggle both
   ways; the index saved twice the same and without a path of this machine;
   the second and third most drawn textures made by the starter profile
   stone-and-concrete through the Materials tab's run (R2b phase 3, in the
   place of R2's Generate and batch: the entries name the profile and its
   hash, one undo step, undone, redone); then every PNG and material
   imported, the material an ORMMaterial3D over its imported maps, drawn by
   the map. It writes `DIR/textures-expect.json`.
   Then R2b's Materials tab (after R3 below): three of Besaid's textures
   queued (the Textures dock's Materialize, the tab's add), every step made
   (256 px first, then the working size), a slider dragged (one undo step;
   the height and the normal made again, the albedo kept; the preview's
   pixels changed under a renderer), the latency of five changes at 512 px
   and five at 1024 px (upscaled x2), undo and redo (the albedo back byte for
   byte), a synthetic roughness map imported and the occlusion skipped (one
   undo step each, the ORM packed from them), the next texture with the
   settings kept, saved at the full size (one undo step, undone, redone; the
   recipe with a store-relative import path, the ORM's occlusion white), the
   open map's textures added; with `--editor-shot`: `materials-studio.png`,
   `materials-hdri.png`, `materials-object.png` beside it.
   Phase 2 (before the save; on the GPU under a renderer, else the CPU): the
   Details band preset changes the height (one undo step), a colour picked in
   the 2D view (as Pick does) with its roughness changes the roughness and its
   mask shows isolated, shape from shading on and the painted light turned
   (a drag, one undo step) change the normal and the arrow follows, the
   occlusion settles in stages (its first directions, then all), the 3D
   preview's pick finds the sphere's UV, the colour check; the save's height
   is a half-float EXR imported as a float texture.
   Phase 4 (after phase 3's): every map added with + map on the first
   texture (one undo step each, in the strip, made, its texture and flag on
   the preview's material; the masks from a source other than the constant
   vary; the detail from the second texture's made maps), undo and redo of
   the detail with its settings, the rim's amount on the material without a
   map made; a synthetic texture with seams imported as the source, the seam
   tool's overlap and scatter (the wrap edges' step against the steps inside,
   the source's and the height's, must drop; one undo step each, Tiling on),
   undone; saved (the material's text with every added map's flag and
   amount, loaded back by Godot with them); a profile with the maps saved
   (its switches, no files), loaded over rock, undone, redone. With
   `--editor-shot`: `maps-<step>.png` (the preview after each map),
   `maps-strip.png`, `seam-overlap.png`, `seam-scatter.png`, `maps-all.png`. Under a renderer the raw
   texture and the default material lit by the studio on three textures
   (the mean OKLab lightness and chroma of the sphere, the line `materials
   lit`), and `materials-isolate.png`, `materials-normal-light.png`,
   `materials-occlusion.png`, `materials-defaults.png`, `materials-bands.png`,
   `materials-lit-raw.png`, `materials-lit-material.png`.
   Phase 5 (after phase 4's): every shortcut of the tab sent as an input
   event (0 to 9 and the brackets to the strip's steps, Tab and Shift+Tab the
   wipe, the arrows the queue, R the setting under the mouse back to its
   default in one undo step, F, ? the list, Ctrl+S a save in one undo step),
   none with the focus on a control outside the tab or with the tab hidden; a
   colour sample picked by a drag in the 2D map (radius 3: the disc's mean,
   one undo step, its mask shown) and one by a click on the preview's plane;
   a texture of Besaid beach picked in the 3D view (the editor's camera put in
   front of a surface: Alt+click queues it, Pick texture with Shift consumes
   the click, an Alt+drag picks nothing); the In map view (M: the open map's
   copy, the live lit material on the texture's surfaces and the classic
   material across the wipe, the map's light, a pick on the piece; a texture
   of another Besaid map loaded on demand); ComfyUI through the fake server
   (`FakeComfy`: progress, a cancel before any answer, Run again, the answer
   cached and the source, from memory and from the cache folder without a
   request, the Source note on Save; its cache file removed after). Under a
   renderer the In map view's pixels, where the texture is drawn (where the
   two sides differ), change with the de-lighting, the normals' strength and
   the metal, and its classic side differs from the live one; `r2b5-shortcuts.png`,
   `r2b5-pick-mask.png`, `r2b5-pick-in-map.png`, `r2b5-in-map.png`,
   `r2b5-in-map-wipe.png`, `r2b5-in-map-changed.png`.
   Phase 6 (after phase 5's; `--materials-only --materials-phase6`: straight
   there): the beach texture whose UV islands propose the most regions (each
   region against its parts' islands, IoU, and the regions' overlap), its
   regions from the islands (one undo step, undone, redone), one by a colour
   picked in the 2D map, one drawn by hand (+ Region, a brush stroke, a
   rectangle, a polygon, the eraser: five undo steps; the eraser undone and
   redone), two profiles on one texture (stone and concrete; metal on one
   region, grass and foliage on another: the metallic inside against outside,
   the grass's backlight there only), a region's setting as its own exception
   (one undo step, the texture's settings untouched), the composite built
   again on the CPU (normals unit length; each region's border in the height
   and the normal against the steps inside; the height's level across the
   border with the levels matched and without), saved (the masks as
   content-named files, the regions' profiles, hashes and exceptions on the
   entry, the grass's backlight in the material; undone, redone; read back
   from the entry alone), Apply to queue with the regioned texture, Make
   again after a region's profile changed (its hash follows); AI estimation
   of the normal step through the fake server with the shipped example
   template (refused with an empty licence and nothing sent, progress, a
   cancel, Run again, the estimate cached and the step's input with no
   placeholder left in the workflow, the strength on top, the cache folder
   without a request, the Source note on Save). Under a renderer
   `r2b6-regions-islands.png`, `r2b6-regions-all.png`,
   `r2b6-composite-normal.png`, `r2b6-composite-object.png`.
   Phase 3 (after the save; profiles into `--remaster-profiles=res://remaster/check/profiles`,
   a layer after the project's, gitignored, removed at the end): the eight
   starter profiles listed (the project's layer), their files as the editor
   writes profiles, and loaded (one undo step);
   the queue's settings saved as a profile (one undo step, undone and redone
   to the same bytes, written again from what it reads the same), loaded,
   kept when the texture changes; a setting pinned to a texture (one undo
   step, the queue's value back to the profile's, the next texture without
   it), edited, cleared, another kept; Apply to queue on the three, cancelled
   after the first (what was made kept, one undo step), then whole (one undo
   step; the entries with the profile, its hash, the kept exception; the
   progress seen), undone and redone; the profile changed (Save, one undo
   step) and its textures marked in the queue and the dock ("Check rock
   changed: 3 textures"), made again (one undo step, the exception kept); an
   R2-era entry (a version 1 recipe, no profile) loaded into the tab
   migrated, edited, saved again; a pack's layer with its own `rock` (a
   registered folder: Godot keeps packs out of the editor; `--selftest-mods`
   mounts real ones), which then wins over the starter one, and taken away. Under a renderer `materials-applied.png`,
   `materials-changed.png`, `profile-<name>.png` (each starter profile on a
   Besaid texture, the studio) and `materials-profile-last.png`. With
   `--materials-only` the editor runs only the map's opening, the where-used
   index and this part (working on the tab).
   Then R3 (`--remaster-looks=DIR/looks`: a stand-in look of Besaid beach
   written there, the editor runs no game): the lighting nodes tagged by ID,
   the key light where the look puts it, the facets at the defaults; the key
   light turned (an undoable transform change, as the gizmo makes), the
   exposure and the fog's mode set in the facets (as the inspector sets
   them), a volume around the beach added (exposure 0.6), each one undo step,
   all four undone and redone; saved (`lighting.tres`, the same twice, no
   absolute ID, no path of this machine), read back; the map opened again (the
   turned key light, the facets' values, the volume; the preview on, the
   editor camera in the volume), saved again the same. It writes
   `DIR/lighting-expect.json`. Then R5: Besaid beach's game data (the walk
   mesh drawn whole, 839 triangles; 49 triggers with the exit named by the
   crossroads; 5 entrances; 21 actor starts; every node the editor's, locked,
   not an addition; the inspector's section), the capture's bird
   `#FBXASC0498_0Shape/*` where the game had it and showing no edit, the
   game's camera and characters; the alignment check on the check's edits,
   then a box added 8 units over the walk mesh's middle triangle (one undo
   step): that triangle flagged blocked and marked, undone, the check as
   before; then the 2D-layer map `ptkl14` (developer save 14) opened: no 3D
   map, its walk mesh (50 triangles), triggers and entrances, its scene
   camera looking at the room and within 0.5 units and 0.5° of the game's
   (the capture), nothing saved. With pictures: `editor-gamedata.png` (the
   beach's game data from above), `editor-gamedata-window.png`,
   `editor-ptkl14.png` and `editor-ptkl14-camera.png` (through its fixed
   camera). The game's check (2) then also takes
   `--lighting-file=DIR/bsil00/lighting.tres --lighting-expect=...`: the map
   drawn remastered, the editor's lighting in force (the key light as turned,
   the exposure, the volume's exposure by the camera, the fog's mode).
3. The game with the editor's textures (`--selftest-materials
   --textures-file=.../textures.tres --textures-expect=DIR/textures-expect.json
   --devsave=7`; "The game's check" below).
4. The round trip (`--remaster-editor-roundtrip`, DIR holding R1a's
   example): opened, each entry shown as its edit and both additions at
   their anchors; saved without an edit, the same entries and additions
   (with the capture: the bird and the marker on it where the game had it,
   saved back without float noise). Then the alignment check on the
   example's additions and replacement (none over the walk mesh) and the
   misplaced test box (flagged).

### The alignment check in the game

```bash
FFX_GAME_DIR=<game> godot --headless --path godot -- --selftest-alignment --devsave=7
```

`--selftest-alignment` (`AlignmentTest`) boots on Besaid beach with R1a's
example registered (as `--selftest-overrides`) and checks the game's data
against the running game: the archive's data in under 0.1 s (the walk mesh
839 triangles, 5 entrances, 49 triggers, 21 actor starts, the line trigger to
map 0x14), the player on the walk mesh (0.00 units off it: the units and axes
line up) and every shown character on it; the capture (into its own
`user://selftest-alignment`, removed at the end, or `--remaster-captures=DIR`,
kept): 73 placed parts with the bird far from the file's place, the morphs,
the camera, the characters; the alignment check on the example's additions and
replacement, then on three pieces made at run time beside the player: a patch
of the walk mesh itself (no issue), the same patch 5 units low (floating) and
a box 8 units over the ground (blocked); the example remade beach
(`stand_in_map.tscn`, a plane fitted to the characters: counted only, most of
it floating or under the sea and sky); last, the player walked forward 1.2 s:
the facing from the game's yaw (as the entrance and actor arrows use it) is
within 30° of the walk (2°).

### The game's check

```bash
FFX_GAME_DIR=<game> godot --headless --path godot -- --selftest-materials --devsave=7
FFX_GAME_DIR=<game> FFX_SHOT_TEST="--selftest-materials --selftest-timeout=150" tools/compare-shot.sh - OUT_DIR   # pictures (Xvfb)
```

`--selftest-materials` makes its textures at run time (in
`user://selftest-materials`, removed at the end): before Besaid beach
loads, it takes the map's two most drawn colour textures from the
where-used answer, a synthetic replacement for the first and a material
generated from it, registered as a layer, the render path forced classic.
Then: the replacement drawn by the classic shaders at load (unshaded, every
other surface its classic texture); Besaid's render path remastered (a
registered overrides layer): the material drawn lit with its maps, the
other surfaces lit from their colour textures, the sky classic, the rig on;
F7 (as a key press) classic, F7 remastered; a replacement for the second
texture registered while the map is shown and bound at once
(`RemasterTextures.Refresh`); materials made from the classic pixels of the
map's six biggest textures, drawn remastered. With `--shot=FILE.png` under a
renderer: FILE.png (classic, with the second replacement) and
FILE-remastered.png. With `--textures-file`, the editor's textures instead
of its own first two.

### The lighting check

```bash
FFX_GAME_DIR=<game> godot --headless --path godot -- --selftest-lighting --devsave=7
FFX_GAME_DIR=<game> FFX_SHOT_TEST="--selftest-lighting --selftest-timeout=400" tools/compare-shot.sh - OUT_DIR       # Compatibility (Xvfb)
FFX_GAME_DIR=<game> FFX_SHOT_TEST="--selftest-lighting --selftest-timeout=400" tools/compare-shot-win.sh - OUT_DIR   # the GPU (Windows export)
```

`--selftest-lighting` on Besaid beach (its files in
`user://selftest-lighting`, removed at the end): the classic picture; the
render path remastered (a registered overrides layer) with no lighting file:
the rig on, the sun along the game's key light, its colour near white, the
ambient not black, the fog as the game's, the colour check against the
classic picture lit from the colour textures, then again with PBR materials
made from the map's six biggest textures; a lighting file registered (a
custom key light, exposure, volumetric fog, VoxelGI, a volume around the
camera): the key light's direction, the volume's exposure in full, volumetric
fog and VoxelGI under Forward+ only (the bake made afresh, removed at the
end); the lighting quality low, medium and high (GI, volumetric fog, SSAO,
the shadows); F7: the classic environment back and its picture as the first.
Without a renderer the colour check is skipped (said in the note). With
`--shot=FILE.png`: FILE.png, FILE-remastered.png, FILE-pbr.png,
FILE-file.png, FILE-classic-again.png. For tuning, `--lighting-variants=FILE.json`
draws and measures variants of the defaults (`[{"name": "...", "props":
{"KeyEnergy": 2.0}}]`; `_CompatExposure` and the like set the rig's
Compatibility scales); `--lighting-debug` prints each map surface's colours.

The pictures show the player's game: keep them out of git.

### The character check

5. R6 (`--editor -- --remaster-editor-character --remaster-characters=DIR`):
   the character list (872 characters, Tidus by his name, the search), c001
   opened (140 motions), the longest motion of its first group played and
   scrubbed (the hips move, scrubbing back gives the same pose), the original
   stand-in humanoid (docs/remake.md, "The stand-in humanoid") saved into
   `DIR/standin` and assigned (21 bones mapped automatically, 2 cloth chains,
   the mouth row, 2 attachments; one undo step, undone and redone), the
   remake driven by the playing motion (its bones within 0.01° of the
   classic skeleton's directions), the automatic mapping, `Spine_Upper` mapped
   by hand to element 5 (the automatic mapping keeps it), saved, both undone
   and redone, saved again byte for byte the same, the character opened again
   with the hand-set row; `DIR/expect.json`. With `--editor-shot=FILE.png` a
   picture of the 3D view. Then the game takes that scene and rig
   (`--selftest-character --character-scene=DIR/c001.tscn
   --character-expect=DIR/expect.json --devsave=7`: docs/remake.md,
   "Retargeting").

### The media check

6. R7a: the game's `--selftest-effects --remaster-effects=DIR/effects
   --devsave=7` (below: "The effect, audio and movie checks") records Besaid
   beach's effects; `tools/make-test-movie.sh DIR/media/test.ogv 40` makes the
   original test video (Godot's movie writer under Xvfb). The editor
   (`--editor -- --remaster-editor-media --remaster-effects=DIR/effects
   --remaster-effect-scenes=DIR/media/effect --remaster-audio=DIR/media/audio/audio.tres
   --remaster-movies=DIR/media/movies/movies.tres --media-video=DIR/media/test.ogv`)
   lists the 15 recorded effects, opens `bsil00-fp15` (120 frames, 8 draws, 2
   textures from the archive), scrubs it to frame 60, assigns the example
   remake (`res://remake/examples/save_sphere_sparkle.tscn`; shown beside the
   replay with its particles), undoes it (classic), redoes it, opens it again
   with the remake; reads the game's sounds (92 named tracks, 10:
   `1_09_battle_theme`, 103 s decoded with its loop; 6,774 sound effects,
   `c355` 21.7 s; 8,665 voice lines), assigns generated stand-ins to music 10
   (loop 1 to 3 s) and the sea `c355`, undoes the sea's alone, redoes it, reads
   them back; lists the 57 movies (`LCI` in `LCI_jp` and `LCI_us`), assigns the
   test video to `LCI` with a subtitle file, undoes, redoes, reads it back and
   previews it. Then the game with what it saved: `--selftest-effects
   --effect-scene=DIR/media/effect/bsil00-fp15.tscn`, `--selftest-audio
   --audio-file=DIR/media/audio/audio.tres` (the battle music),
   `--selftest-movie --devsave=22 --movies-file=DIR/media/movies/movies.tres`.

### The wizard check

7. R4 (`tools/remaster-wizard-check.sh OUT_DIR`, also alone): the editor under
   Xvfb (`--editor -- --remaster-editor-wizard --remaster-wizard-dir=DIR/wizard
   --remaster-maps=DIR/wizard/maps --remaster-textures=res://remaster/check/wizard-textures`):
   Besaid beach from the map browser's selection (the scopes: the area and all
   maps listed), its pass staged without an undo step or a project file (53
   textures with kinds, scores and reasons; at least ten materials with three
   profiles or more, their staged entries and files, the thumbnails; GI staged; the
   note; the pictures and the colour check under a renderer); a texture rejected
   (one undo step, undone, redone, the state file following), another made again
   with a different profile (one undo step, undone, redone), the pictures drawn
   again; the map accepted (one undo step: every material entry, the rejected one
   without, the remade one with its new profile, the render path, status and notes,
   `lighting.tres` with GI; undone: the project as before; redone); a batch of
   `slik08`, `ptkl14` (2D layers) and `mtgz03` paused while the first is made (the
   state file says paused after one), resumed (two staged, one skipped, the
   summary with the time per map). It writes `DIR/wizard/wizard-expect.json`. Then
   the game with what it accepted (`--selftest-wizard --wizard-expect=...`
   through `tools/compare-shot.sh`: pictures and the colour check in
   `DIR/wizard-game`), a headless batch paused in one editor session and resumed in
   the next (`--remaster-wizard --wizard-pause-after=1`, then `--wizard-resume`),
   and the classifier's accuracy (`--selftest-wizard-classifier`, below).

### The effect, audio and movie checks

```bash
cd godot
FFX_GAME_DIR=<game> godot --headless --path . -- --selftest-effects --devsave=7 --remaster-effects=/tmp/fx
FFX_GAME_DIR=<game> godot --headless --path . -- --selftest-audio --devsave=7
../tools/make-test-movie.sh /tmp/test.ogv 40
FFX_GAME_DIR=<game> godot --headless --path . -- --selftest-movie --devsave=22 --movie-video=/tmp/test.ogv
```

- `--selftest-effects`: the example registered for `bsil00-fp15`; on Besaid
  beach every effect draw has an owner (30 of 30, 15 field programs), the remake
  plays where the classic effect is (its draws' centre, every frame) while it
  plays, its 8 classic draws are left out and the others still drawn; F8
  brings the classic effect back and stops the remake, F8 again remakes it;
  the recording is written and reads back (120 frames, 8 draws, 2 textures).
  With `--shot=FILE.png` under a renderer: `FILE.png` and `FILE-classic.png`,
  the camera at the save sphere.
- `--selftest-audio`: generated stand-ins replace the sea effect `c355` on the
  beach (the game's silenced, the game's fade-in followed; its second layer
  `c356` and the other effects stay the game's) and every music id; in the
  deck battle (field 3, asked for by the check) the battle music 10 is
  replaced, the game's music volume held at 0 and its mix at 0.007 RMS against
  0.080 with F8 (the game's music back, the stand-in stopped), 0.007 again
  with F8 again. `--audio-file=FILE`: the editor's file, its music checked in
  the battle.
- `--selftest-movie`: developer save 22 (Luca) opens `LCI_jp.webm` (the voice
  language's); the test video replaces it with a subtitle: the replacement
  plays over the picture, advancing as the game's movie does (frames 4 to 94
  while it went 0.37 to 3.37 s), the subtitle shows, the game's movie sound is
  silent (the mix 0.017 RMS against 0.075 with F8, which brings the game's
  movie back). `--movies-file=FILE`: the editor's file.

### The wizard's checks in the game

```bash
FFX_GAME_DIR=<game> godot --headless --path godot -- --selftest-wizard-classifier --devsave=7
FFX_GAME_DIR=<game> FFX_SHOT_TEST="--selftest-wizard --selftest-timeout=500" tools/compare-shot.sh - OUT_DIR       # the pass in the game (Compatibility, Xvfb)
FFX_GAME_DIR=<game> FFX_SHOT_TEST="--selftest-wizard --selftest-timeout=500" tools/compare-shot-win.sh - OUT_DIR   # the GPU (Windows export)
```

- `--selftest-wizard-classifier` (`ClassifierCheck`): every texture of
  `godot/remaster/classifier-labels.tsv` classified from its classic pixels and how
  its map draws it (each labelled map's classic import loaded), against its label:
  the accuracy overall, leave one area out (the prototypes refitted without the
  area scored: the honest number), per kind (recall and precision), the confusion
  matrices, the tuning areas, Zanarkand and the held-out areas apart. Fails under
  60 % overall. `--classifier-dump=FILE` writes the features and uses; with
  `--no-game --classifier-features=FILE` it classifies from such a file (tuning,
  2 s); `--classifier-verbose` prints every texture with its reasons.
- `--selftest-wizard` (`WizardTest`) on Besaid beach: the classic picture; with
  `--wizard-expect=FILE` the editor's accepted files registered as layers (the map
  remastered by its own render path, each accepted texture drawn with its made
  material, the rejected one without, the remade one's material, the lighting
  file's GI and exposure), else the wizard's pass run in the game (classified,
  made at most `--wizard-size` px, 512 by default, on the CPU, the lighting, the
  exposure tuned once); the colour check against the classic picture (with a
  renderer); F7 classic again. `--shot=FILE.png`: FILE.png, FILE-remastered.png.

## Exports

The plugin is editor only: its C# is compiled only with `TOOLS` (the
editor's build; an export's is without), `export_presets.cfg` excludes
`addons/*` and `remaster/edit/*`, and `tools/export-files.py check` fails an
export that holds either (`project.godot` still names the plugin's
`plugin.cfg`; the game ignores it).

## Not yet

- The packs' layers are applied in the game but not shown in the editor;
  it edits the project's layer only.
- No thumbnails in the browser.
- 2D-layer maps (the painted rooms) open with the game's data only (their
  layers are not drawn in the editor); remade maps' own scenes are not
  opened (the editor always shows the classic import; overrides on a remade
  map's tagged nodes are made by hand; the alignment check takes one with
  **Check scene...**).
- Script-moved objects are where the game had them at its last capture of
  the map (one moment: the birds fly on in the game), the morphs as they were
  then; without a capture at their file place.
- The game's data (R5): the triggers, positions and models the scripts set
  with constant arguments only (computed ones, and what events change later,
  are not shown); the 3D maps' follow cameras (the camera worker's polar
  set-ups) are not read; walk-mesh pages other than the first are drawn but
  the alignment check uses the first; the triangles' encounter, sound and
  water bits are in the data, not coloured.
- The where-used index covers the maps' 3D models only (not characters,
  2D layers, effects or menus).
- Characters (R6): the dock edits the project's characters only (a pack's are
  shown when the registry finds them, not edited); the mapping UI is a list
  with a number per bone (no picking bones in the 3D view, no gizmo for a
  bone's offset: set `Offset` in the rig's inspector); the alignment is
  automatic or the rig's `Align` by hand; the face table's rows other than the
  mouth are added in the rig's inspector; the motions are the character's own
  groups (not the events' or battles' `.mgrp` files).
- Godot prints a few "Failed loading resource" lines while new PNGs are
  being imported (a scan and an import after a texture's first edit); they
  load from the files meanwhile.
- The ComfyUI client (upscaling and the AI estimation of maps) is untested
  against a real server; the AI's template is placeholder nodes only (a
  workflow of one's own is needed). Regions: a region's maps are a whole graph
  each (two to four regions cost two to four times the passes); the material's
  own amounts (parallax depth, emission energy, the added maps' amounts) are
  one per texture; the UV islands come from the maps' 3D models (the
  where-used index's), not from characters or effects.
- Lighting (R3): the lighting file layers whole, not per field; LightmapGI
  is not drawn (no lightmap UVs); the VoxelGI bake sees albedo textures, not
  the PBR materials' emission; the editor previews the key light from the
  game's last look, which the game writes only when it shows the map; a
  volume blends by the camera, not the player; the Steam Deck budgets are
  not measured on the device.
- The game's painted shore layers blend in gamma in the game; lit (the
  `BlendedParts = Lit` choice) and in linear light they come out paler, so
  they stay classic by default, and the shore under them stays paler than
  the classic one (Besaid's lagoon).
- A headless editor save prints Godot's own "Parameter "t" is null" (no
  thumbnail without a renderer).
- Effects (R7a): only the field programs and the spell overlays have ids; the
  battle's shared effects (`bat_eff`), the op system's records and the fixed
  keys stay classic. The recording is one run's first 120 updates of each
  effect; the Media dock edits the project's layer only (a pack's remakes are
  listed, not edited).
- Audio (R7a): a replaced sound effect plays where the game plays it, without
  its pan; the voice browser lists the US banks; the dock's loop fields apply
  when a file is assigned (edit them in the inspector after).
- The wizard (R4): the classifier is right on about half of unseen areas' textures
  (leave one area out: 55 %; grass, sand and keep classic well, rock against stone
  half, wood, metal and plaster seldom), so its choices are a first pass to review;
  the review's pictures need a renderer (the headless batch stages without them);
  a map the game has not shown here has no look (the lighting's defaults, a sky
  without the game's colours); a discarded pass's files stay in the texture
  folders.
- Movies (R7a): no classic movie picture in gd-ffx yet, so no classic preview
  and nothing drawn for a movie without a replacement; the replacement plays at
  its own rate (not re-synchronised to the game's frame if it drifts); the
  subtitle file is set in the inspector.

## The code

| File | What |
|---|---|
| `RemasterPlugin.cs` | the plugin: the dock and inspector, the actions (one undo step each), save on Ctrl+S, play |
| `EditorClassic.cs` | the archive in the editor: the folder, `FfxGame`, `ClassicLibrary`, the catalogue, the developer saves, the play plan |
| `MapBrowserDock.cs` | the dock |
| `MapEditRoot.cs` | the edit scene's root: builds the map, applies the project's layer, the parts and their marks, the undoable operations |
| `MapEditSave.cs` | the scene's state as override entries and an additions scene |
| `MapStore.cs` | the project's per-map files, held while the editor runs; the stable text |
| `PartInspector.cs` | the part's inspector section |
| `TextureDock.cs` | the Textures dock: the browser, the compare, the uses, the actions |
| `TextureActions.cs` | replace, upscale, revert, edit, the entry of a made material (one undo step each, through `RemasterPlugin.SetTexture`) |
| `ProfileStore.cs` | the project's material profiles as the editor writes them (R2b phase 3), the textures whose profile changed |
| `QueueBake.cs` | a run of Apply to queue or Make again: one texture after another at the full size, GPU or CPU |
| `TextureStore.cs` | the project's texture index and files, the import |
| `MaterialsTab.cs` | the Materials tab (R2b): the queue, the steps and their settings, undo, the pipeline's results, save; profiles, exceptions, Apply to queue, Make again |
| `MaterialsTab.Keys.cs`, `.Pick.cs`, `.InMap.cs`, `.Comfy.cs` | the tab's shortcuts, colour picks, the In map preview, ComfyUI in the Source step (R2b phase 5) |
| `MaterialsTab.Regions.cs`, `MaterialRegionStore.cs` | regions within one texture: the Regions box, the tools, undo, the entry's region dictionaries and masks (R2b phase 6) |
| `MaterialsTab.Ai.cs`, `ComfyAi.cs`, `comfyui/` | AI estimation of maps: the AI mode, the user's workflow and its placeholders, the licence, the cache; the example template (R2b phase 6) |
| `InMapScene.cs` | the In map preview's copy of a map (the live and classic surfaces of the texture) |
| `RemasterPlugin.Pick.cs` | textures picked in the open map (Alt+click, Pick texture) |
| `FakeComfy.cs` | the editor check's stand-in ComfyUI server (loopback) |
| `MaterialPreview.cs` | the tab's 3D preview: the objects, the camera, the compare wipe, studio, HDRI and the map's light |
| `MapView2D.cs` | the tab's map in 2D (zoom, pan, 2x2, the split, picking, the painted light's arrow) |
| `PreviewObject.cs` | the game piece that draws a texture, as one mesh |
| `hdri/` | the preview's HDRIs (CC0, Poly Haven; README) |
| `WhereUsed.cs` | the where-used index and its cache |
| `ComfyUpscaler.cs` | the optional ComfyUI upscaling, and the client every workflow runs through (`Run`) |
| `LightingEdit.cs` | the map's lighting as nodes, the preview, the save of `lighting.tres` |
| `LightingFacet.cs` | the ambient, fog and environment nodes (a part of `MapLighting` as the node's properties) |
| `VolumeNode.cs` | a lighting volume's node (the box) |
| `GameDataEdit.cs` | the game's data as nodes (R5): walk meshes, triggers, entrances, actors, cameras, the capture's characters and placements; the alignment check's marks |
| `EditorSelfTest.cs` | the check |
| `CharacterDock.cs` | the Characters dock (R6): the list, motions, mapping, cloth, face, attachments, status |
| `CharacterEditRoot.cs` | a character's edit scene: the classic import with its motions, the remake beside it, driven |
| `CharacterStore.cs` | the project's character scenes and rigs, held while edited |
| `RemasterPlugin.Characters.cs` | the character actions (open, assign, map, align, positions, cloth mode, status, save; one undo step each) |
| `CharacterEditorTest.cs` | the character check |
| `MediaDock.cs` | the Media dock (R7a): the Effects, Audio and Movies tabs |
| `EffectEditRoot.cs` | an effect's edit scene: the recording replayed (scrub), the remake beside it |
| `MediaStore.cs` | where R7a's files go (the project's or a check's), their text, the recorded effects |
| `RemasterPlugin.Media.cs` | open an effect; assign or clear an effect's remake, a sound, a movie (one undo step each) |
| `MediaEditorTest.cs` | the media check |
| `WizardDock.cs` | the Wizard dock (R4): scope, run, pause, resume, the maps, the review |
| `Wizard.cs` | the wizard's engine: the batch, a map's pass (import, classify, bake, lighting, imports, pictures, exposure, colour check), a texture made again, pictures again |
| `WizardState.cs` | the wizard's saved state: the batch, each map's staged pass |
| `WizardRender.cs` | the review's before/after pictures in a viewport of its own |
| `RemasterPlugin.Wizard.cs` | the review's actions (decide, accept, discard; one undo step each), the headless batch |
| `WizardEditorTest.cs` | the wizard's editor check |

Game side (R2): `RemasterTexture`, `RemasterTextureSet`, `PbrRecipe` (the
resources), `RemasterTextures` (the layers, the merge, the images and
materials, the binding on the classic materials), `PbrBuilder` (images, the
material over the maps) and `Texturing/` (R2's generators, Lanczos),
`MaterialProfile` and `MaterialProfiles` (R2b phase 3: a profile, the
profiles' layers, the hash, a texture's exceptions), `MaterialPipeline` (R2b: the steps, their
settings, the CPU's runs off the main thread), `Materialize/` (R2b phase 2:
`MaterializeGraph` the passes of every map, `CpuBackend` and `Kernels` the
CPU's kernels, `GpuBackend`, `GpuKernels` and `GpuPipeline` the GPU's,
`MaterializeCheck` the parity and speed check, `--selftest-materialize`; phase 3:
`MaterialBaker` a bake on the CPU and the files' images, `ProfileSheet` the starter
profiles measured, `--materialize-profiles`), `RemasterLook` and `MapLook` (the lit
materials, the swap), `RemasterLighting` and `RenderPaths` (the rig, the
choice), `MaterialsTest`; `ClassicMaterials` binds the replacements and
notes each map material's modes and defines (`ffx_map_shader`).

Game side (R3): `MapLighting`, `LightingVolume` (the resources),
`LightingLayers` (the files and their layers), `RemasterLighting` (the rig:
the defaults from the look, the volumes, GI, the quality), `LookCache` (the
looks for the editor), `ColourBalance` (the colour check), `LightingTest`;
`RemasterLook` (`ffx_lit_gamma`, the blended parts), `ClassicMaterials`
(premultiplied output under the linear one).

Game side (R6, `godot/src/Characters/`): `CharacterRig`, `RigBone`,
`ClothChain`, `FaceMap`, `AttachMap` (the rig's resources), `RemadeCharacter`
(the remade scene's root), `Retargeter` (the driver), `RigBuilder` (the
automatic rig), `CharacterScene` (scene and rig files), `CharacterCatalogue`
(the character list), `CharacterMaterials` (the presets), `StandInHumanoid`,
`CharacterTest`; `StateView3D` drives remade characters and their
attachments, `ClothView` gives the game's cloth points and the classic cloth
vertices.

Game side (R7a, `godot/src/Remaster/`): `EffectOwners` (the ids),
`EffectRemakeView` (the remakes in the game, the classic draws left out),
`EffectRecorder` (the recordings), `RemadeEffect` (`godot/src/Remake/`, the
remake's root), `RemasterSound`, `RemasterSoundSet`, `RemasterMovie`,
`RemasterMovieSet` (the resources), `AudioReplacements` (the layers),
`AudioReplacementView` (the sounds in the game), `MovieView` (the movies),
`Replacements` (F8), `EffectsTest`, `AudioTest`, `MovieTest`; in the extension
`fxowner.rs` (who made each draw) and `media.rs` (the sound commands, the
movies); `ffx-import`'s `sounds.rs` (the game's sounds by id, decoded).

Game side (R4, `godot/src/Remaster/Wizard/`): `TextureFeatures` (a texture's colour
and statistics), `MapTextures` (how a loaded map draws each texture), `TextureClassifier`
(the kinds, the rules, the prototypes, the reasons), `WizardPass` (a map's pass: the
classification, the lighting and GI, the exposure, the note), `ClassifierCheck`
(`--selftest-wizard-classifier`), `WizardTest` (`--selftest-wizard`).

Game side (R5): `MapGameData` (the archive's walk meshes, scene cameras,
entrances and workers in render space, from `FfxGame.classic_map_game_data`),
`MapCapture` (the game's capture for the editor; `StateView3D` writes it),
`Alignment` (the check), `AlignmentTest`.

Game side: `GameHost.ClassicGame` (the running game's object, else the
editor's), `ClassicLibrary.Load` in the editor (the glTF images kept in
memory; Godot's importer mesh instances made mesh instances, as the game has
them), `MapOverrideRuntime.SplitSurface` (one segment as a node, shared with
the game), `SavedOverridesTest` (`--selftest-saved-overrides`).
