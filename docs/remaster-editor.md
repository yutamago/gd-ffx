# The remaster editor

gd-ffx's editor plugin for the remaster (docs/remaster-plan.md, R1b to R3, R5):
browse the game's maps, open one as an editable scene of its classic parts,
move, hide, remove, replace and add parts with Godot's own tools, and save
the edits as the map's override files (docs/remake.md, "Overrides"), which
the game applies; replace textures, make physically based materials for them
and draw a map remastered ("Textures" and below), and light it: the game's
lights and fog as nodes, sky, GI, volumes ("Lighting"); see the game's own data on
the map (walk mesh, triggers, entrances, cameras) and check that remade geometry
lines up with it ("The game's data"). What it writes into the
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
  the maps using it. **Edits** says `replaced`, `PBR`, or the pack a change
  comes from.
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
- **Generate material**, **Edit material**, **Recipe**: "Materials".
- **Revert**: back to the classic texture (no replacement, no material).

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
own), `Recipe` (how the material was made, `PbrRecipe`), `Source` (where the
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

**Generate material** makes the texture's physically based material from
its colour texture (the replacement, else the classic one) with the
generators ported from ffx-remix (MIT, the same author;
`godot/src/Remaster/Texturing/`: `MapGenerator`, `Lanczos`), as its
`PbrRecipe` says (the texture's recipe, the one **Recipe** opened in the
inspector, else the defaults):

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
`ffx_remaster/hdri_dir`. The recipe sliders below and the batch stay until
R2b's phase 3.

### The batch

Below the dock: materials for many textures at once. The scope is the open
map's textures, the selected map's area, every map the project changes, or
every map (the where-used index); **Skip done** leaves out textures that
have a material. **Generate** makes up to 48 into the review list, one at a time
on a worker thread so the editor stays usable (each with its albedo;
nothing is written yet); untick the bad
ones, **Accept ticked** writes the rest as one undo step, **Discard** drops
them all. Run again for the next 48.

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
`Source`. The ComfyUI client has not been run against a real server yet.

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
   drawing it, a material generated for the second from its classic pixels,
   each one undo step, undone and redone; Besaid's render path set to
   remastered (written), the map drawn remastered, the view's toggle both
   ways; the index saved twice the same and without a path of this machine;
   the batch over the open map (two made for review, one unticked, the other
   kept as one undo step, undone, redone); then every PNG and material
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
   is a half-float EXR imported as a float texture. Under a renderer the raw
   texture and the default material lit by the studio on three textures
   (the mean OKLab lightness and chroma of the sphere, the line `materials
   lit`), and `materials-isolate.png`, `materials-normal-light.png`,
   `materials-occlusion.png`, `materials-defaults.png`, `materials-bands.png`,
   `materials-lit-raw.png`, `materials-lit-material.png`. With
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
- Godot prints a few "Failed loading resource" lines while new PNGs are
  being imported (a scan and an import after a texture's first edit); they
  load from the files meanwhile.
- The ComfyUI client is untested against a real server; there is no PBR
  model template yet (ffx-remix's `pbr-from-albedo` is the model to port).
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
- The dock's recipe has no live preview (the Materials tab has); the batch
  has no per-texture recipe.
- A headless editor save prints Godot's own "Parameter "t" is null" (no
  thumbnail without a renderer).

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
| `BatchPanel.cs` | the batch and its review |
| `TextureActions.cs` | replace, upscale, generate, revert, edit (one undo step each, through `RemasterPlugin.SetTexture`) |
| `TextureStore.cs` | the project's texture index and files, the import |
| `MaterialsTab.cs` | the Materials tab (R2b): the queue, the steps and their settings, undo, the pipeline's results, save |
| `MaterialPreview.cs` | the tab's 3D preview: the objects, the camera, the compare wipe, studio, HDRI and the map's light |
| `MapView2D.cs` | the tab's map in 2D (zoom, pan, 2x2, the split, picking, the painted light's arrow) |
| `PreviewObject.cs` | the game piece that draws a texture, as one mesh |
| `hdri/` | the preview's HDRIs (CC0, Poly Haven; README) |
| `WhereUsed.cs` | the where-used index and its cache |
| `ComfyUpscaler.cs` | the optional ComfyUI upscaling |
| `LightingEdit.cs` | the map's lighting as nodes, the preview, the save of `lighting.tres` |
| `LightingFacet.cs` | the ambient, fog and environment nodes (a part of `MapLighting` as the node's properties) |
| `VolumeNode.cs` | a lighting volume's node (the box) |
| `GameDataEdit.cs` | the game's data as nodes (R5): walk meshes, triggers, entrances, actors, cameras, the capture's characters and placements; the alignment check's marks |
| `EditorSelfTest.cs` | the check |

Game side (R2): `RemasterTexture`, `RemasterTextureSet`, `PbrRecipe` (the
resources), `RemasterTextures` (the layers, the merge, the images and
materials, the binding on the classic materials), `PbrBuilder` and
`Texturing/` (the generators), `MaterialPipeline` (R2b: the steps, their
settings, the CPU's runs off the main thread), `Materialize/` (R2b phase 2:
`MaterializeGraph` the passes of every map, `CpuBackend` and `Kernels` the
CPU's kernels, `GpuBackend`, `GpuKernels` and `GpuPipeline` the GPU's,
`MaterializeCheck` the parity and speed check, `--selftest-materialize`), `RemasterLook` and `MapLook` (the lit
materials, the swap), `RemasterLighting` and `RenderPaths` (the rig, the
choice), `MaterialsTest`; `ClassicMaterials` binds the replacements and
notes each map material's modes and defines (`ffx_map_shader`).

Game side (R3): `MapLighting`, `LightingVolume` (the resources),
`LightingLayers` (the files and their layers), `RemasterLighting` (the rig:
the defaults from the look, the volumes, GI, the quality), `LookCache` (the
looks for the editor), `ColourBalance` (the colour check), `LightingTest`;
`RemasterLook` (`ffx_lit_gamma`, the blended parts), `ClassicMaterials`
(premultiplied output under the linear one).

Game side (R5): `MapGameData` (the archive's walk meshes, scene cameras,
entrances and workers in render space, from `FfxGame.classic_map_game_data`),
`MapCapture` (the game's capture for the editor; `StateView3D` writes it),
`Alignment` (the check), `AlignmentTest`.

Game side: `GameHost.ClassicGame` (the running game's object, else the
editor's), `ClassicLibrary.Load` in the editor (the glTF images kept in
memory; Godot's importer mesh instances made mesh instances, as the game has
them), `MapOverrideRuntime.SplitSurface` (one segment as a node, shared with
the game), `SavedOverridesTest` (`--selftest-saved-overrides`).
