# The material workbench (R2b, plan)

R2 can turn a texture into a physically based material, but the workflow
is slow. The recipe's sliders sit in a side dock, the preview is small, every
map is made at once, and a change shows only after the maps are made again.
R2b replaces that with a view built for the job: one texture, one map at a
time, a big lit preview that changes while a slider moves, and profiles that
carry the settings over to the next texture.

Status: phase 1 implemented (2026-11-18, "Phase 1: what there is"); phases
2 to 6 planned. Decisions taken with the user are marked as such; the ones
taken while implementing are in "Decisions".

## What it reuses and what it learns from

- **R2's generators** (`godot/src/Remaster/Texturing/MapGenerator.cs`, ported
  from ffx-remix): de-lighting to albedo, height, normal, roughness, ambient
  occlusion, metallic, emission, Lanczos upscaling. They stay as the CPU
  implementation (below, "Speed").
- **R2's storage**: a texture's entry in `res://remaster/textures/textures.tres`,
  its maps and material in its own folder named by content (undo and redo
  never touch a file), gitignored because the maps are made from the game's
  textures. R2b writes the same way.
- **R3's lighting**: the remastered path's rig and tone mapping, so the
  preview lights a material as the game will.
- **ffx-remix's map generator panel**: per-map options and a live preview,
  limited to a 256 px copy. Its de-lighting is what R2b's albedo step uses.
- **Materialize** (BoundingBoxSoftware, GPL-3.0). Decision (user): read its
  code, reimplement its concepts, copy nothing. Concepts taken (each named in
  the step that uses it):
  - the expensive work (blurred bands of the image) kept apart from the
    cheap work (weighting and combining the bands), which runs in the
    preview's shader as uniforms, so those sliders cost nothing;
  - the band equaliser: seven blur bands from fine to coarse, a weight and a
    contrast for each (a negative contrast inverts a band), with presets, the
    band sizes scaled to the texture's size;
  - colour masks picked by clicking: a hue, saturation and lightness
    distance with a weight for each and a soft threshold, and a view of the
    mask alone;
  - progressive sampling: AO (and height from a normal map) add a few
    directions per frame to a running mean, so the preview settles in a
    fraction of a second instead of blocking;
  - shape from shading: a painted light direction read from the texture,
    turned into slope;
  - height-aware seam blending for tiling;
  - the before/after split.
  Its weaknesses, which R2b avoids: no real albedo (its diffuse step only
  pulls light toward a local average), maps that never update when an
  earlier map changes, all work at full size (slow on big textures), 8-bit
  results, no user presets, batch only through the clipboard.

## The view

A **Materials** tab in Godot's main area, beside 2D, 3D, Script and Game
(an editor plugin's main screen), so it gets the centre of the window.

```
┌ 2D │ 3D │ Script │ Game │ Materials ────────────────────────────────────────────────┐
│ Queue (3)          │                                            │ 2 · Height          │
│ ▣ bsil_sand_01  ●  │                                            │ ┌─────────────────┐ │
│ ▢ bsil_rock_02     │        big 3D preview, drag to rotate      │ │ the map, tiled  │ │
│ ▢ bsil_rock_03     │                                            │ │ 2×2, zoom, pick │ │
│ + add …            │     raw  ◀━━━━━━━━━┃━━━━━━━━━▶  material    │ └─────────────────┘ │
│                    │                                            │ Bands ▁▃▅▇▇▅▃ [Det] │
│ Profile: Rock ▾    │ Plane Cube Cylinder Sphere Object In map   │ Contrast ───●────   │
│ [Save as profile]  │ Studio │ HDRI: overcast ▾ │ The map's light │ Invert □  [Reset]   │
│ [Apply to queue]   │ Tile 2×  │ Light ◐                          │ Generate Import Skip│
├────────────────────┴────────────────────────────────────────────┴─────────────────────┤
│ 0 Source ✓│1 Albedo ✓│2 Height ●│3 Normal│4 Roughness│5 Metallic│6 AO│7 Emission│+ map│Save│
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- **The queue** (left): the textures being worked on, each with its state
  (raw, in progress, saved, stale because its profile changed). Arrow keys
  or a click switch texture; the current settings stay, so the next texture
  shows at once with them.
- **The preview** (centre), as large as the window allows (below).
- **The step panel** (right): the current map in 2D (zoom, pan, tiled 2×2
  to show seams, a before/after split, click to pick colours) and only this
  step's settings.
- **The step strip** (bottom): every map as a thumbnail, in order; done,
  current, skipped or stale; a click jumps to it.

## The preview

- **Objects**: plane, cube, cylinder, sphere, **Object** (the game piece that
  uses the texture, with its own UVs, from the classic import) and **In map**
  (the open map, or a map that uses the texture, in the preview, the camera
  at the piece, so the material is seen where it will be).
- **Controls**: drag to rotate, scroll to zoom, middle drag to pan, Shift+drag
  to turn the light, Alt+drag to turn the environment, F to frame; tiling and
  offset of the UVs.
- **Compare**: a wipe across the preview. Left side: the classic look (the
  game's shader), the raw texture lit as plain colour, or the material with
  one map switched off. Right side: the material.
- **Lighting**:
  - **Studio**: key, fill and rim light, the key turnable.
  - **HDRI**: decision (user): ship them. Four small CC0 HDRIs from Poly
    Haven (1k, about 6 MB together; sun, overcast, sunset, interior), their
    source and licence recorded beside them, plus a folder of one's own
    (editor setting `ffx_remaster/hdri_dir`).
  - **The map's light**: R3's lighting of a map that uses the texture (its
    key light, ambient, fog and sky as the game's look gives them).
- **Tone mapping and exposure** as the remastered path, so the preview looks
  as the game does. Renderer: Forward+ where there is one (the Windows editor
  on Vulkan); the WSL editor's Compatibility renderer shows the same steps
  with less light quality.

## The steps

Every step makes one map. Entering a step makes its map from the profile.
Every setting change shows in the 2D map and the 3D preview at once. A change
upstream makes the maps that depend on it again, automatically (a dependency
graph: Source → Albedo → Height → Normal → AO and bent normal; Albedo →
Roughness, Metallic, Emission and the masks). Each step can instead take a
file (**Import**: a hand-made map) or a constant (**Skip**), and has
**Reset** per setting and for the step.

Decision (user): metallic and roughness, no specular map; offer every map
Godot's materials take. The core steps are always in the strip; the others
are added per profile with **+ map**, so the strip stays short.

### Core

0. **Source.** The classic texture or its replacement; upscaling (×1, ×2, ×4:
   Lanczos, or ComfyUI when configured); the working size; the alpha mode
   (opaque, cut-out, blended, from the classic material); a tiling check
   (2×2) and, if it needs one, the seam tool (height-aware overlap: across
   the seam the taller feature wins; or a scatter of rotated copies), applied
   the same way to every map so they stay aligned.
1. **Albedo.** R2's de-lighting: painted shading out (strength, size, edge
   keeping), creases and cracks lifted, highlights pressed down, slow colour
   casts flattened, white balance; plus the hot and dark spot removal of
   Materialize's diffuse step and a saturation and lightness check against
   the classic texture (the colour check of R3, per texture). The 2D view's
   split shows classic against albedo.
2. **Height.** From the albedo (or the original, or an imported normal map,
   progressively). The band equaliser (seven bands, a weight and a contrast
   each, presets: details, shapes, cracks), up to three colour samples with
   a height each (click "this is high", "this is low"), final contrast, bias
   and an S-curve gain. Kept at 16 bits (no terracing in parallax).
3. **Normal.** From the height: slope strength, the band equaliser over the
   normals (presets smooth, crisp, mid), shape from shading (the painted
   light's direction, set by dragging an arrow on the 2D view, and how much
   of the slope comes from it), angularity (a bevelled, faceted look), final
   contrast. OpenGL convention (green up), as Godot reads it.
4. **Roughness.** A base value; from brightness and local contrast (R2's);
   up to three colour samples with a roughness each; the metallic parts'
   roughness; contrast, bias and a fine-detail overlay. Shown as roughness,
   or as smoothness with a toggle.
5. **Metallic.** A constant, or a colour mask (pick the metal's colour;
   hue, saturation and lightness weights, soft threshold, pre-blur),
   contrast, bias, a fine-detail overlay. Most FFX textures skip it.
6. **AO.** From the height and the normal: horizon sampling over many
   directions (progressive in the preview, all at once in the bake), radius,
   depth, the mix of normal-based and height-based occlusion, bias and power.
   Also makes the **bent normal** (the mean open direction) when that map is
   on.
7. **Emission.** Usually skipped: a brightness or colour mask, colour and
   energy.

### Added with "+ map"

- **Bent normal** (from the AO step), for better occlusion of light from the
  sky.
- **Rim** mask: soft edge light (cloth, velvet, moss); a colour mask or a
  constant, rim strength and tint.
- **Clearcoat** mask and gloss: a lacquer layer (polished wood, wet stone,
  car paint); a colour mask or a constant.
- **Anisotropy** direction: stretched highlights (hair, brushed metal,
  grain); the direction field from the texture's own grain (the local
  gradient's orientation, smoothed), or a fixed angle.
- **Subsurface scattering** mask and **transmittance** (colour and
  thickness, the thickness from the height or brightness): skin, wax, leaves.
- **Backlight**: light through thin surfaces (leaves, paper, cloth); a colour
  mask with a tint.
- **Refraction**: the distortion of things behind glass or water; strength
  from the normal or a mask.
- **Detail**: a second, finer texture over this one (a mask from a colour,
  the height or brightness; a detail albedo and normal from a file or from
  another texture's made maps, at its own UV scale), for close-up detail
  beyond the classic texture's resolution.

Maps the material does not take are not saved, but some are made inside the
pipeline as inputs (Materialize's edge map, the curvature of the normals:
used for worn edges in roughness and as a mask choice).

### Save

All maps side by side; the material's own settings (UV scale, parallax
depth and layers, emission energy, alpha, cull, the clearcoat, rim and
subsurface amounts); then **Save** (this texture) or **Apply to queue**
(below), and **Save as profile**.

## Profiles

A profile is every step's settings plus the material's, saved as
`res://remaster/profiles/<name>.tres`: original work, not game data, so it
goes in git and in packs (the later pack wins, as for textures).

- **Starter profiles**: rock, stone and concrete, sand and dirt, grass and
  foliage (cut-out), wood, metal, fabric, plaster and painted walls; tuned on
  FFX textures (Besaid, Luca, Macalania, Bevelle).
- **A texture's entry** stores its profile, its own exceptions (only the
  settings that differ), the profile's content hash at bake time and its
  colour samples. Colour samples are colours, not positions, so they carry
  over to the next texture.
- **Profile changed**: the queue and the Textures dock mark textures whose
  profile changed since they were made ("Rock changed: 37 textures"), and
  one action makes them again.
- **Migration**: R2's `PbrRecipe` on existing entries becomes a profile's
  settings (the old fields map onto the albedo, height, normal, roughness,
  AO, metallic and emission steps), so existing materials stay editable.

## Several textures

- **Adding**: from the Textures dock (several at once), a click on a piece in
  the open map (its textures in the inspector, **Materialize**), the
  where-used search, all of a map's textures, or a drag from the FileSystem.
- **Switching**: arrow keys or a click; the settings stay, the preview shows
  the next texture with them; a setting changed for one texture only can be
  pinned to it (an exception) instead of the queue.
- **Apply to queue**: every texture in the queue made with the current
  settings (and its own exceptions) in the background, a progress bar, a
  cancel, the results in the strip and the queue as they finish; one undo
  step for all. This replaces R2's batch panel; the Textures dock becomes a
  browser with a **Materialize** button (its recipe sliders go).

## Speed

The workflow stands or falls with this. Decision (user): GPU generators.

- **One pipeline on the GPU**: every generator as fragment-shader passes on
  offscreen render targets (separable blurs, the band pyramid, the guided
  filter of the de-lighting, slopes to normals, horizon AO, colour masks).
  Offscreen canvas passes work in both of Godot's renderers, so the WSL
  editor runs them too. The pipeline is runtime code (`src/`), not editor
  only, so the game can run it as a check on the GPU.
- **Cheap and expensive settings**: weights, contrasts, biases, gains and
  strengths are uniforms of the combining passes (and of the preview's
  shader where possible): a slider changes the next frame. Radii, the
  upscale and source changes rebuild the bands, on the GPU.
- **Working size**: the preview runs at a working size (1024 by default,
  2048 on request); the bake runs the same shaders at full size, so what is
  seen is what is saved. Progressive passes (AO, bent normal, height from a
  normal map) settle over a few frames in the preview and run in full in the
  bake.
- **Targets**: a slider shows in the preview within 33 ms at 1024 px (Intel
  Arc 140T, Vulkan); a texture's full set of maps baked at 2048 px in under
  a second, at 4096 px in under four.
- **The CPU generators stay** for headless runs (no GPU there: the checks,
  command-line batches) and as the reference. They gain the same algorithms
  (bands, colour masks, shape from shading); a parity check holds the two
  within a tolerance per map.
- **Formats**: albedo and normal 8-bit PNG (sRGB and linear), height as a
  16-bit or half-float file, ORM packed (occlusion, roughness, metallic) as
  Godot's `ORMMaterial3D` reads it; the other masks 8-bit, single channel
  where Godot allows.

## Undo and safety

- Settings changes in the tab are undoable (a slider drag is one step);
  switching texture or step is not.
- Save and Apply to queue are one undo step each; the files are named by
  content, so undo and redo never overwrite one.
- Nothing made from the game's textures enters git: maps and materials stay
  in the gitignored texture folders; profiles and HDRIs (CC0) do.

## Phases

Each phase is usable on its own.

1. **The view on the existing generators.** The Materials tab, the queue,
   the preview (objects incl. the game piece, rotation, the compare wipe,
   tiling, studio, HDRI and the map's light), the core steps (source,
   albedo, height, normal, roughness, metallic, AO, emission) with Import,
   Skip and Reset, the dependency graph, Save into R2's storage. The preview
   computes small first and refines. The point: judge the workflow early.
2. **The GPU pipeline and Materialize's concepts.** The generators as shader
   passes; the band equaliser, colour masks, shape from shading, progressive
   AO; the CPU generators brought to the same algorithms; the parity check;
   16-bit height; the speed targets measured on the GPU.
3. **Profiles and the queue.** Profiles (save, load, starter set, packs),
   exceptions, Apply to queue, profile changed and make again, the migration
   of R2's recipes; the Textures dock reduced to a browser, the batch panel
   removed.
4. **The other maps.** Bent normal, rim, clearcoat, anisotropy, subsurface
   and transmittance, backlight, refraction, detail; the seam tool.
5. **Working fast.** Picking textures in the scene, the In map preview,
   keyboard (1 to 8 and the strip for steps, Tab to compare, arrows for the
   queue, Ctrl+S to save, R to reset a setting), colour picking in the 2D
   view, ComfyUI upscaling in the source step.
6. **Later.** Regions within one texture, each with its own profile: many
   FFX textures are atlases with rock, grass and wood side by side. AI
   estimation of maps where a model's licence allows it (recorded per
   texture, as R2 records upscaling).

## Phase 1: what there is

The **Materials** tab (`godot/addons/ffx_remaster/MaterialsTab.cs`) on R2's
CPU generators:

- **Adding textures**: the Textures dock's **Materialize** (several at once:
  Ctrl or Shift+click in its list), **Materialize** beside each texture in the
  part inspector's "FFX part" section, the tab's **+ Open map** (every texture
  the open map draws) and **+ Selected** (the dock's selection). A click in
  the queue or the arrow keys switch texture with the settings kept; **Its
  settings** loads a texture's saved recipe instead (one undo step).
- **The preview** (`MaterialPreview.cs`): plane, cube, cylinder, sphere and
  **Object** (`PreviewObject.cs`: the surfaces drawing the texture, from the
  open map, else from the classic import of the first map the where-used
  index names, with their UVs and vertex colours, at most 48 surfaces);
  drag to orbit, wheel to zoom, middle drag to pan, Shift+drag turns the
  light, Alt+drag the environment, F frames; UV tiling (1 to 8); the compare
  wipe (left: the raw texture lit as a plain colour, or the classic look: the
  game piece's own classic material on the Object, the texture unshaded on
  the primitives; right: the material); **Studio** (key, fill, rim), **HDRI**
  (the four shipped ones, then `ffx_remaster/hdri_dir`'s) and **The map's
  light** (R3's resolved lighting of the open map when it draws the texture,
  else of the first map that uses it: its key light, ambient, background and
  tone map; the game's look from `LookCache`, the defaults' direction when
  the game has not shown the map yet; no fog). Tone mapping, exposure,
  saturation and contrast from that map's lighting, else R3's defaults.
- **The steps** (`godot/src/Remaster/MaterialPipeline.cs`): Source (upscale,
  working size, full size, tiling; or an image of one's own), Albedo,
  Height, Normal, Roughness, Metallic, Occlusion, Emission, each with
  **Generate**, **Import** (a file, resized to the working size) and
  **Skip** (a constant), **Reset step** and a reset per setting. The step
  panel shows the map in 2D (`MapView2D.cs`: wheel zoom, drag pan, double
  click for the whole map, 2x2 tiling, the before/after split against the
  source) and only that step's settings; the strip shows every map with its
  state (✓ made, … making, ⇩ imported, – skipped).
- **The dependency graph**: every map is kept with a signature of its
  settings, mode, file and its inputs' signatures; a change makes again
  exactly the maps downstream of it (a height change: height, normal,
  occlusion; the albedo stays). Settings only the material reads (parallax
  depth, emission energy) make no map at all.
- **Small first, then refined**: every change asks for all maps at 256 px,
  then at the working size (1024 by default; never above the texture's size
  after the upscale), the current step's chain first, off the main thread
  (one run at a time, a new change cancels the run going; the texture's
  pixels and the imported files are read on the main thread before).
- **Save**: the maps made again at the full size (`MaxSide` after the
  upscale) in a run later changes do not cancel, then written as R2 writes
  them (`TextureStore.WriteMaterial`: content-named PNGs, the
  `ORMMaterial3D`'s text), the settings as the entry's `PbrRecipe`
  (`Version` 2), one undo step of the global history.
- **Undo**: every setting, mode, import, reset and "Its settings" is one step
  of the editor's global history; a slider drag is one step (the maps follow
  it live, the step is committed at its end).

**Latency** (a setting changed → the current step's map shown at 256 px →
at the working size → every map at the working size), on Besaid beach's
textures with R2's CPU generators, the development machine (WSL2, Xvfb editor,
2026-11-18; the check's log line `ffx editortest: materials latency`):

| Change | working size | small | refined | all maps |
|---|---|---|---|---|
| a step's own setting (median of 5) | 512 px (the texture's size) | 3 ms | 18 ms | 62 ms |
| the de-lighting (every map after it) | 512 px | 42 ms | 160 ms | 220 ms |
| a step's own setting (median of 5) | 1024 px (upscaled x2) | 3 ms | 60 ms | 200 ms |
| the de-lighting | 1024 px | 40 ms | 406 ms | 653 ms |
| the upscale (every map, new sizes) | 1024 px | 10 ms | 102 ms | 696 ms |
| Save (every map at the full size, 1024 px, and the files) | | | | 1.4 s |

So a slider shows within a frame or two at 256 px and settles in a tenth to
half a second at 1024 px; the plan's GPU target (33 ms at 1024 px, phase 2)
is for the refined map.

Not in phase 1 (planned later): profiles, Apply to queue, the band
equaliser, colour masks, progressive AO, the GPU pipeline, 16-bit height,
the other maps, the seam tool, In map, colour picking, keyboard steps.

## Decisions

Taken while implementing phase 1 (2026-11-18):

- **The tab's name.** Godot names an editor plugin's main screen after the
  plugin (`_GetPluginName`), one main screen per plugin, so the remaster
  plugin's name is now "Materials" (`RemasterPlugin.MainScreen`); its docks
  keep their titles.
- **Settings on `PbrRecipe`.** The workbench's settings are new fields of R2's
  `PbrRecipe` (a mode and a file per step, the generators' other options,
  `EmissionEnergy`, `WorkingSide`) with defaults, and `Version` (1: R2's,
  2: the tab's), so every old entry loads as it was. An R2 recipe loaded in
  the tab ("Its settings") is migrated: emission on where R2's threshold made
  one. R2's Generate and batch still run `PbrBuilder.Make` (R2's order)
  until phase 3 removes them.
- **The graph as the plan draws it.** Height from the albedo by default
  (`HeightFrom`: or the source, as R2 did), roughness, metallic and emission
  from the albedo (R2: the source). Occlusion is computed from the height
  only (as R2's) but depends on the normal in the graph, ready for phase 2's
  normal-based occlusion.
- **Skip's constants.** Albedo: the source as it is; height: flat (no
  parallax); normal: flat; roughness: the base value; metallic: the value;
  occlusion: white; emission: none. Metallic and emission are skipped by
  default (most FFX textures).
- **Imported files.** While editing, the step holds the chosen file's path;
  Save copies its pixels into the texture's folder
  (`import-<step>.<hash>.png`) and the saved recipe names that store-relative
  path, so no path of the machine is kept and the import stays editable.
- **The preview's material** is Godot's `ORMMaterial3D` over the maps (what
  Save writes), not the game's lit shader (`RemasterLook`), which adds the
  baked vertex light; the In map view (phase 5) shows that.
- **The compare wipe** draws two views of one world (two cameras with cull
  masks, the object twice on two layers), joined by a shader at the wipe.
- **The map's light** is R3's `RemasterLighting.Resolve` over the map's
  lighting (the open map's as edited, else its layers) and its look, applied
  to the preview's own light and environment (the rig itself is not used: it
  changes global shader parameters); fog is left out on a small object.
- **HDRIs** (decision with the user: ship them): Poly Haven's `meadow_2`
  (sun), `kloofendal_overcast`, `venice_sunset` and `lebombo` (interior),
  1k, CC0, 5.8 MB, in `godot/addons/ffx_remaster/hdri/` with a README naming
  the sources and authors. Exports leave out `addons/*` already, so no
  export rule changed.
- **The save size.** Save runs the same pipeline at the full size (the
  texture after the upscale, at most `MaxSide`), not at the preview's
  working size.

## Checks

- **The editor check** (`tools/remaster-editor-check.sh`) drives the tab on
  Besaid: queue three textures, step through, change a setting and see the
  preview's pixels change, import and skip a map, save, undo and redo, save
  a profile, switch texture with it, apply to the queue, a profile changed
  and made again; pictures under Xvfb (`FFX_EDITOR_SHOT=1`). Phase 1 has the
  parts without profiles and the queue's apply (docs/remaster-editor.md,
  "Checks").
- **A game-side check** on the GPU (`--selftest-materialize`, the Windows
  export through `tools/compare-shot-win.sh`): the pipeline's time per
  slider change and per bake against the targets, and the GPU maps against
  the CPU maps (parity).
- **The materials and lighting checks** (`--selftest-materials`,
  `--selftest-lighting`) with a material made by a profile.
- The quick sweep (`tools/sweep-devsaves.sh`) for anything that touches the
  game's drawing.

## Risks

- **Precision in the Compatibility renderer.** The band pyramid and the
  height want float render targets. If the WSL editor's Compatibility
  renderer gives only 8-bit offscreen targets, it previews at lower precision
  and its bakes go through the CPU generators instead; the Windows editor
  (Vulkan) has float targets. Phase 2 measures this first.
- **The speed targets** come from Materialize's experience (full-size blits
  are fast; band rebuilds on big textures are not) and Godot's offscreen
  passes; phase 2 measures them on the GPU before the CPU generators are
  changed to match.
- **Two implementations** of every generator (decision: accepted) can drift;
  the parity check runs in the editor check and on the GPU.

## Alongside

Decision (user): R5 (the game's data in the editor) runs alongside R2b. R4
(the remaster wizard) waits: it should choose a profile per texture (by the
texture's name, its map's area and a colour classification) rather than
apply one recipe to all, so it builds on phase 3.
