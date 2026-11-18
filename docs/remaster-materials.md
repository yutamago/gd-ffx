# The material workbench (R2b, plan)

R2 can turn a texture into a physically based material, but the workflow
is slow. The recipe's sliders sit in a side dock, the preview is small, every
map is made at once, and a change shows only after the maps are made again.
R2b replaces that with a view built for the job: one texture, one map at a
time, a big lit preview that changes while a slider moves, and profiles that
carry the settings over to the next texture.

Status: phases 1 to 3 implemented (2026-11-18, "Phase 1: what there is",
"Phase 2: what there is", "Phase 3: what there is"); phases 4 to 6 planned. Decisions taken with the user are marked as such; the ones
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

## Phase 2: what there is

**One graph, two backends** (`godot/src/Remaster/Materialize/`). Every map is
made by a graph of small passes (`MaterializeGraph`): a pass is a kernel over
images with its settings. The graph is built the same way for the GPU
(`GpuBackend`: each pass a `SubViewport` drawing one rectangle with the
kernel's canvas shader into its own offscreen target, `GpuKernels`) and for
the CPU (`CpuBackend`: the same kernels in C#, `Kernels` for the shared
math), so the two run the same algorithm pass for pass. A pass is named by a
stable slot and carries a signature (its kernel, size, settings and its
inputs' signatures): a build runs only the passes whose signature changed,
on the GPU in the next frame (Godot draws sibling viewports in the order
they entered the tree, so the passes, put in the graph's order, all draw in
one frame, inputs first, and before the preview's views), on the CPU when a
map is read.

- **Precision.** Measured first (2026-11-18): offscreen canvas targets with
  `use_hdr_2d` are 32-bit floats under Compatibility (the WSL editor, Mesa)
  and half floats under Forward+ (Vulkan); negative values and values above
  1 survive. So Compatibility bakes on the GPU too (the plan's fallback to
  the CPU is not needed). The maps as saved (albedo, normal, ORM, emission,
  the masks) are 8-bit targets, the height and every intermediate float.
- **Blurs** are a Gaussian pyramid: the image halved (a 1 3 3 1 kernel) to the
  level where the radius is 2 to 4 of its pixels, a separable Gaussian there
  (integer taps, at most ±8), read back bilinearly by the pass that uses it.
  A radius of 64 px costs about what one of 4 px does; pyramids are shared
  (the seven bands of an image use one).
- **Cheap and expensive.** Band weights and contrasts, the final contrast,
  bias and gain, strengths, the occlusion's strength, bias and power, the
  roughness's and metallic's contrast, bias and overlay are settings of the
  combining passes only: a change draws those and what follows (a height
  band's weight: 31 of 147 passes at 1024 px; the roughness's contrast: the
  roughness, the ORM). Radii, the upscale, the source and the colour samples
  of the height change the bands (a band rebuild: about 50 passes).
- **Progressive occlusion.** In the preview the occlusion comes in chunks of
  four directions, one chunk a frame (eight chunks at most), each its own
  pass, their mean in a last pass; the bake takes every direction in one
  pass. The CPU's preview posts the first chunk, then all.
- **The preview** samples the passes' targets directly (no copy): the albedo
  and emission targets are 8-bit with sRGB values, which Godot's material
  decodes as it does an imported texture (measured, both renderers); the
  height target is float.
- **The bake** (Save) builds the same graph at the full size in one frame
  (no progressive part) and reads every map back; the height is saved as a
  half-float OpenEXR (`height.<hash>.exr`, single channel), imported by
  Godot as a float texture (`Rgbh`), the parallax's heightmap without 8-bit
  terraces; ORM packed as `ORMMaterial3D` reads it (R occlusion, G
  roughness, B metallic).
- **Headless** (the checks, `--headless`): the CPU backend, the same graph.
  The editor setting `ffx_remaster/materials_gpu` (default on) chooses the GPU
  when there is a renderer.

**The steps, with Materialize's concepts** (concepts only, our own code):

- **Source**: the upscale as a GPU pass too (Lanczos-3, separable, linear light,
  premultiplied, as `Lanczos.Resize`).
- **Albedo**: R2's de-lighting on the GPU (the log luminance; its light from a
  blur, or mixed with a self-guided filter whose variance is the blurred
  squared residual; occlusion and cavities from a quick height; highlights
  and, new, dark spots evened, each with a soft knee; slow colour casts
  flattened; the mean kept), then **the colour check**: the mean OKLab
  lightness and chroma of the classic texture and of the albedo, shown in
  the step panel, and **Match classic** (default 1) pulls the albedo's means
  to the classic's.
- **Height**: the input's perceptual luminance; up to three **colour samples**
  (a colour picked in the 2D view or on the object, a height each, a pull);
  smoothing; **the band equaliser**: seven bands, the image blurred by 0,
  0.5, 1, 2, 4, 8 and 16 classic texels, each measured against a wide
  average (32 texels), a weight and a contrast each (a negative contrast
  inverts the band), presets Default, Details, Shapes and Flat, Cracks, Fine
  only; the weighted sum x 2.5 around mid grey, then the final contrast,
  bias and an S-curve (Schlick's gain); invert.
- **Normal**: the slope of the smoothed height (Sobel or Scharr); **shape from
  shading**: the source's high-pass (its luminance less its blur by the
  shading size) read as slope along **the painted light** (an angle, set by
  dragging the arrow on the 2D view or its slider), the slope across the
  light kept, mixed by an amount; **the band equaliser over the slopes**
  (presets Crisp, Mids, Smooth); **angularity** (each slope pulled to the
  same tilt in its own direction: bevels); the final contrast (the tilt
  scaled, z raised to it, renormalised). OpenGL convention. Made in floats
  and saved in 8 bits; the curvature and the occlusion read the floats.
- **Curvature** (internal, the "edge" map): the divergence of the normals,
  for **edge wear** in the roughness.
- **Roughness**: R2's brightness and local contrast (the local variance now the
  blurred squared residual); up to three colour samples with a roughness
  each; the metallic parts' own roughness; contrast, bias; **fine detail**
  (x (1 + high-pass x amount)); edge wear.
- **Metallic**: a constant (Skip), the brightness mask (R2's), or a **colour
  mask** (the metal's colour) on a pre-blurred copy; contrast, bias, fine
  detail.
- **Occlusion**: per pixel, directions spread evenly from a per-pixel random
  start, twelve steps out to the radius each from a per-pixel random offset
  (an integer hash, the same on both backends); the horizon's sine from the
  height (x the depth) and the tilt of the normal against the direction,
  mixed by **From the normals**; their mean; strength, power, bias.
- **Colour masks** (`Kernels.ColourMask`): hue (circular), saturation and
  lightness closeness weighted (defaults 1, 0.5, 0.5; the hue counts only as
  far as both colours are saturated: grey has no hue), through a soft
  threshold (0.88 to 0.97). **Isolate** shows a sample's mask in the 2D view.

**The step panel**: the band equalisers as seven small vertical sliders with
their presets (one undo step each; a drag one step), colour samples as a
swatch, **Pick** (the next click in the 2D view or on the object in the 3D
preview: a ray against the object's triangles gives the UV), **Isolate** and
clear; the painted light's arrow on the 2D view in the normal step; the
colour check under the 2D view in the albedo step. Generate, Import, Skip,
Reset and undo as in phase 1.

**The preview's compare** was lit by the ambient light only in phase 1 (Godot
culls lights by a camera's cull mask, and the lights were on the material's
layer): the raw side looked dark and the material "far brighter". With the
lights on both layers, the default material and the raw texture lit by the
studio agree on Besaid's textures within 1 % of lightness and 5 % of chroma
(the editor check's `materials lit` line: e.g. raw L 0.841 C 0.025, material
L 0.835 C 0.025), so the defaults stay as they are.

**The parity check** (`--selftest-materialize`, and its no-game form on a
synthetic texture): every feature on, the CPU's maps against the GPU's bake
at 1024 px. Limits: an 8-bit map's mean difference at most 2 of 255 and its
99th percentile at most 12; the height's (floats) 0.004 and 0.03. On a failure
it lists the passes furthest apart. Measured (2026-11-18, the synthetic
bricks, Mesa llvmpipe; mean/p99/max of 255): source 0.54/1/1, albedo
0.08/1/1, normal 0.32/1/2, roughness 1.14/8/18, metallic 0.13/0/49 (a few
pixels at a colour mask's edge), occlusion 0.31/1/3, emission 0/0/0, ORM
0.53/7/49; height 0.0008/0.0017; the same under Compatibility and Forward+.
On the Arc 140T (Vulkan, half floats) with Besaid's texture: source 0.59/1/1,
albedo 0.19/1/1, normal 0.56/2/2, roughness 0.35/2/3, metallic 0/0/0,
occlusion 0.40/1/2, emission 0/0/10, ORM 0.25/1/3; height 0.0019/0.0027. The
check also fails on any pass that reads a pass drawn after it (the GPU
backend checks the order at every structural change): the parity check found
one such bug (a blur asked for twice in one build moved behind its first
reader).

**Speed** (`--selftest-materialize`, 2026-11-18). Besaid beach's most used
opaque colour texture (512 px, upscaled x2 to 1024 px), every feature on; a
change is timed from the settings to its passes drawn and the GPU finished (a
one-pixel read back), so it includes waiting for the frame:

| | Intel Arc 140T, Vulkan (Windows export) | Mesa llvmpipe, Compatibility (Xvfb) | Mesa lavapipe, Forward+ (Xvfb, synthetic) | Target |
|---|---|---|---|---|
| a cheap change at 1024 px (a height band's weight: 31 of 137 passes) | **15 ms** | 190 ms | 122 ms | 33 ms |
| the roughness's contrast (the roughness and the ORM) | 11 ms | 69 ms | 38 ms | |
| a band rebuild (the height's smoothing: 52 passes) | 20 ms | 283 ms | 177 ms | |
| the occlusion settled (16 directions) | 4 frames | 4 frames | 4 frames | |
| building the graph (the CPU's part of a change) | 0.8 ms | 1.1 ms | 1.3 ms | |
| bake, every map read back, 1024 px | 143 ms | 1.0 s | 0.55 s | |
| bake, 2048 px | **460 ms** | 2.9 s | 1.6 s | 1 s |
| bake, 4096 px | **2.5 s** | 12.0 s | 6.0 s | 4 s |
| the CPU's bake at 1024 px (the same graph) | 1.3 s | 4.0 s | 3.6 s | |

On the GPU every target is met; the software renderers (the WSL editor)
are 5 to 10 times slower and still interactive (a slider in a tenth to a
fifth of a second). The first build of a session costs more: the kernels'
shaders are compiled on first use (the first bake at 1024 px took 1.4 s on
the Arc against 0.14 s after, 4.6 s under llvmpipe). In the editor under Xvfb (llvmpipe) the tab's own
latency: a change drawn in 120 to 270 ms at 512 to 1024 px, the occlusion
settled in 0.4 to 0.7 s, Save at 1024 px 0.7 to 1.4 s; headless (the CPU) 0.1
to 0.7 s drawn, 2.3 s with every map at 1024 px (phase 1's CPU path: 0.65 s;
the 16-direction occlusion is most of it).

Not in phase 2: the seam tool, profiles and the queue's apply (phase 3), the
other maps (phase 4), height from a normal map and the bent normal (they come
with phase 4's maps).

## Phase 3: what there is

**Profiles** (`godot/src/Remaster/MaterialProfile.cs`, `MaterialProfiles.cs`).
A `MaterialProfile` is a `PbrRecipe` with a `Title` and a `Note`: every
step's settings and the material's (`MaterialProfiles.Props`: all of
`PbrRecipe`'s settings but the imported files, the preview's working size and
the version). Saved as `res://remaster/profiles/<name>.tres`, the name made
from the title (`Stone and concrete` → `stone-and-concrete.tres`), as text
written by `MaterialProfiles.ToText`: the title, the note, then only the
settings that differ from the defaults, in `PbrRecipe`'s order, floats as
their shortest decimals; the same settings give the same bytes, and a diff
shows what a change did. Layers as the textures': the project's folder, the
editor's own folder when it is elsewhere (`--remaster-profiles=DIR`, the
editor check's), each mounted pack's `res://mods/<pack>/remaster/profiles/`
in load order, folders registered at run time; the later layer wins per
name. A profile's **hash** (`MaterialProfiles.Hash`: the settings' values,
not the title, the note or the file's form) is what a texture remembers.

**In the tab** (under the queue): the profile picker (every layer's, the
pack named; loading one sets the queue's settings, one undo step), the state
("● 3 settings differ from Rock"), **Save** (the queue's settings over the
profile), **Save as...**, **Revert** (the queue back to the profile),
**Rename...** (the file, the title, and every texture of the project made
with it, all one undo step), **Delete** (the textures made with it keep
their settings and show "no such profile"). Each is one undo step of the
global history that swaps the file's text (`RemasterPlugin.SaveProfile`,
`RenameProfile`, `DeleteProfile`; `ProfileStore`), so undo and redo give the
same bytes back.

**The queue's settings and the exceptions.** The tab keeps the queue's
settings (`MaterialsTab.QueueSettings`: what a change edits, kept when the
texture changes) and each texture's exceptions (settings pinned to it). The
current texture is shown and made with the queue's settings and its
exceptions over them (`Recipe`). Beside every setting (and every band
equaliser, every colour sample) a pin: ○ the queue's (a click pins the value
now to the texture, and the queue's value goes back to what it was before
its last change, so "I changed it for this one" becomes an exception), ● the
texture's (marked on the label too; a click unpins it). A pinned setting's
changes go to the texture's exception, the others to the queue; **Clear**
unpins all. Each pin, unpin and change is one undo step. The queue shows
`● n` beside a texture with exceptions.

**A texture's entry** (`RemasterTexture`) keeps, besides the `Recipe` (every
setting as baked, as before): `Profile` (the profile's name: the winning
layer's is meant, so a pack's profile of the same name takes its place),
`ProfileHash` (its hash at bake time) and `Exceptions` (setting → value: the
settings that differed from the profile when it was made, the steps'
imported files among them). Colour samples are settings like the others
(colours, not positions), so they carry over with the queue's settings, or
stay with one texture as exceptions. Without a profile, the entry has none
of the three and its recipe is its own.

**Apply to queue** (`QueueBake`): every texture of the queue made with the
queue's settings and its own exceptions, at the full size, one after
another: on the GPU (a `GpuBake` each: one frame, the read back) when the
tab has a renderer, else on the CPU (`MaterialBaker.Cpu`, the same graph, on
a worker); the texture's pixels and imported files read on the main thread
first. A progress bar, **Cancel** (the texture being made is dropped, the
rest not made; what was made is kept, as one undo step), each result's
albedo as the texture's icon in the queue and in the strip ("2/3: name") as
it comes. Each result's files are written as it comes (content-named, so
undo and redo never overwrite one; the steps' imported files copied beside
the maps), and the entries are set as **one undo step** at the end.

**Profile changed.** A texture whose profile's hash is not the one it
remembers is marked in the queue (`⟳ Rock changed`) and the Textures dock
(the Edits column, and "Rock changed: 37 textures" under the list), and
**Make again** (in either) makes every such texture with the profile as it
is now and its own exceptions (in the background, one undo step). The
exceptions are worked out again against the new profile, so one that the
profile has taken over stops being an exception. `MakeWith(ids, profile)`
makes textures with a profile without the queue (the editor check; R4's
wizard will choose a profile per texture).

**Migration.** An entry's `Recipe` of R2 (`Version` 1: the dock's Generate
and the batch) and of phases 1 and 2 (2, 3) is a texture's own setting set:
**Its settings** loads it as the queue's (migrated: `MaterialPipeline.Migrate`,
R2's emission threshold turned into the emission step), with no profile;
saved again it is a version 3 recipe, linked to a profile if one is chosen.
Nothing is rewritten unasked: old entries load and draw as they were.

**The Textures dock** is a browser now: the list (open map, where used,
project; search), the before/after compare, the uses, **Replace...**,
**Upscale**, **Revert**, **Edit material**, **Materialize** (the selected
textures into the tab's queue), the profile marks and Make again. Its
recipe, **Generate material** and the batch panel went (`BatchPanel.cs`
removed; `PbrBuilder.Make`, R2's chain, removed with them: the game's checks
make their materials by a starter profile on the CPU graph,
`MaterialBaker.CpuImages`).

**The starter profiles** (`godot/remaster/profiles/`, committed: settings,
original work; no texture or picture in git): rock, stone and concrete, sand
and dirt, grass and foliage, wood, metal, fabric, plaster and painted walls.
Each was tuned on the game's textures of its kind (each profile's `Note`
names them; found through the where-used index, in Luca, Besaid, Baaj,
Mi'ihen and Zanarkand: FFX's own texture names say what they are only in
the HD remaster's re-made maps, `lchb00_rockwall_06` and the like) with
`--selftest-materialize --materialize-profiles=DIR`
(`Materialize/ProfileSheet.cs`): every profile baked on the CPU on its four
textures, the colour check (the albedo's mean OKLab lightness within 0.03 of
the classic texture's, the chroma within 20 %), the roughness's mean in its
kind's range, the metallic's mean (metal 0.5 to 1, the others under 0.05),
and a contact sheet per texture in DIR (classic, albedo, height, normal,
roughness, occlusion, metallic, the classic and the material lit by one
light), looked at. Measured (2026-11-18, 512 px at most,
the CPU; the means over each profile's five textures, the colour check within
0.003 of lightness and 3 % of chroma on every texture):

| Profile | Roughness (range) | Spread (p90 - p10) | Metallic | Normals' tilt (1 - z) | Height (parallax) |
|---|---|---|---|---|---|
| rock | 0.83 (0.70 to 0.95) | 0.13 | 0 | 0.064 (about 21°) | 0.03 |
| stone and concrete | 0.80 (0.75 to 0.97) | 0.12 | 0 | 0.017 | 0.015 |
| sand and dirt | 0.91 (0.85 to 1) | 0.07 | 0 | 0.016 | 0.012 |
| grass and foliage | 0.72 (0.50 to 0.85) | 0.18 | 0 | 0.012 | none |
| wood | 0.70 (0.55 to 0.85) | 0.13 | 0 | 0.013 | 0.012 |
| metal | 0.38 (0.25 to 0.65) | 0.08 | 0.61 (0.5 to 1) | 0.010 | 0.008 |
| fabric | 0.89 (0.80 to 1) | 0.06 | 0 | 0.012 | 0.006 |
| plaster and painted walls | 0.82 (0.75 to 0.95) | 0.13 | 0 | 0.011 | 0.008 |

The first settings (R2's normal strengths, roughness ranges of 0.1 to 0.4)
gave normals tilted 2 to 4 degrees and roughness maps of one grey (spread
0.02 to 0.05): the slopes are per pixel of a smooth height, so the starter
profiles' normal strengths are 4 to 14 and their roughness ranges 0.5 to
1.4, with a bias where the range moved the mean. Metal's spread stays low
(its metallic parts take one roughness, and FFX's metal panels are painted
evenly; its limit is 0.07); its metallic mask is the brightness one (dark
parts are rust and dirt), pre-blurred, which is crude: a colour sample does
better per texture. The lit columns of the sheets, and the editor check's
`profile-<name>.png` (each profile on a Besaid texture, the studio, Godot's
renderer), are the pictures looked at.

Not in phase 3: the other maps (phase 4), regions with their own profile
(phase 6), choosing a profile per texture automatically (R4).

## Decisions

Taken while implementing phase 3 (2026-11-18):

- **A profile is a `PbrRecipe` subclass** (`MaterialProfile`: a title and a
  note more), not a resource holding one: one flat text file, the same
  settings code (copy, hash, migration) for both.
- **Profiles are written by our own text writer** (`MaterialProfiles.ToText`),
  not `ResourceSaver`: only the settings that differ from the defaults, in
  `PbrRecipe`'s order, floats as their shortest decimals (`1.65`, not
  `1.64999997615814`), no `uid`; Godot loads it as any `.tres`. Short files,
  readable diffs, the same bytes for the same settings.
- **A profile holds no imported files and no working size**: an import is a
  texture's own (Save as profile turns a step on Import into Generate; the
  file stays a texture's exception), the working size is the view's.
- **Textures name their profile by name**, not by path: the winning layer's
  is meant, so a pack's profile of the same name takes the project's place
  (as a pack's texture entry does), and a texture made with the project's
  shows as changed under a pack whose profile differs.
- **The entry keeps the whole recipe as baked** besides the profile, its hash
  and the exceptions: an entry loads and can be made again without its
  profile (a pack without it, a deleted profile), and an R2-era entry is
  simply one without a profile.
- **Exceptions are the settings that differ from the profile at bake time**:
  the pinned ones and also the queue's changes not saved into the profile
  (so Make again gives the same material until the profile changes). Made
  again, they are worked out anew against the profile, so one the profile
  has taken over is dropped.
- **Pinning gives the queue its value before the last change back** (else
  the profile's): "I changed this for this texture" in one click; pinning a
  setting never changed keeps the queue's value as it is.
- **Cancel keeps what was made** (one undo step for it): with a long queue,
  most of the work is not thrown away; Apply again makes the rest (and the
  same settings give the same files: nothing written twice).
- **Profile operations are global-history undo steps that swap the file's
  text**, and Rename moves the textures that name the profile in the same
  step.
- **The textures' alpha mode is not a profile's** (it comes from the texture
  and its classic material); the grass and foliage profile is tuned for
  cut-out textures (no parallax, soft normals).
- **The starter profiles leave the upscale at 1**: the upscale is a
  remaster's choice of size, not a material's kind.
- **The game's checks make their materials by a starter profile** on the CPU
  graph (`MaterialBaker`), and R2's chain (`PbrBuilder.Make`) went with the
  dock's Generate and the batch: one way to make a material.
- **A pack's profile in the editor check is a registered layer**, not a
  mounted `.pck`: Godot turns its packed data off in the editor, so
  `load_resource_pack` fails there (tried). The folder a pack's profiles
  come from is registered as the layer after the project's
  (`MaterialProfiles.Register`, the same lookup as ModLoader's packs), and
  `--selftest-mods` mounts real packs with profiles (the later pack's per
  name, a starter one no pack has stays the project's). Mounting a pack also
  invalidates the profiles' listing (`ModLoader.Mount`).
- **The texture index is read fresh** (`RemasterTextures` loads it with
  `CacheMode.Ignore`, was `Replace`): found by the check's undo of Apply to
  queue. Replace updated the editor's live entries in place from the file,
  and Godot's text leaves out fields at their defaults, so a profile emptied
  by an undo kept its old name in memory.

Taken while implementing phase 2 (2026-11-18):

- **Canvas passes in SubViewports, both renderers.** Not RenderingDevice
  compute: Compatibility (the WSL editor) has none, and its offscreen float
  targets are 32-bit (measured), so one implementation serves both.
- **One graph for both backends** (`MaterializeGraph`), each kernel written
  twice (C# and GLSL) and held together by the parity check, rather than two
  separate pipelines.
- **Pass signatures, not step signatures.** The dependency graph is at pass
  granularity: a cheap setting draws the combining pass and what reads it.
- **The blur is a Gaussian pyramid** (halving, then a Gaussian of 2 to 4 level
  pixels, read bilinearly), on both backends, instead of R2's three box
  blurs: every radius costs the same, and the band equaliser's seven bands
  share one pyramid.
- **Bands in classic texels** (0, 0.5, 1, 2, 4, 8, 16; the wide average 32),
  scaled with the upscale, not in fractions of the image as Materialize
  does: FFX's detail lives at the classic texel's scale, whatever the
  texture's size.
- **The local variance** (the de-lighting's guided filter, the roughness's
  detail) is the blurred squared residual from the local mean, not
  mean-of-squares minus square-of-mean: the latter cancels catastrophically
  in half floats.
- **The height** is the band sum x 2.5 (`MaterializeGraph.HeightBandGain`) around
  mid grey, so the final contrast's default (1) gives a usable range; no
  gamma on the bands (our luminance is perceptual already).
- **The hue counts as far as both colours are saturated** in a colour mask
  (Materialize weighs it fully): grey's hue is noise.
- **The mask defaults** (lightness weight 0.5, threshold 0.88 to 0.97) were
  tightened on Besaid's sandy textures, where the looser ones selected all.
- **Normals in floats** for the curvature and the occlusion: the curvature
  from the 8-bit normal turned one step of rounding into 8 of 255.
- **Progressive occlusion** in chunks of four directions (one pass each, a
  mean pass after), not a ping-pong accumulation: the chunks' signatures make
  "start again" automatic when the height or normal changes.
- **Matching the classic colour** (OKLab means of lightness and chroma) is on
  by default; it only corrects what the de-lighting moved.
- **16-bit height as a half-float OpenEXR**: Godot's PNG loader takes 8 bits
  of a 16-bit PNG; a single-channel half EXR imports as a float texture
  (`Rgbh`) the heightmap reads as it is.
- **`OcclusionSamples` defaults to 16** (8 in phase 1), four a frame in the
  preview; `Version` 3 marks a recipe made with phase 2's algorithms (2 loads
  unchanged).
- **The preview's lights on both views' layers** (the compare side was lit
  by the ambient light only).
- **R2's dock and batch** (`PbrBuilder.Make`) keep R2's generators until
  phase 3 removes them (done: phase 3).

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
  a profile, switch texture with it, pin an exception and clear it, apply to
  the queue (a cancelled run, then whole), undo and redo, a profile changed
  and made again, an R2-era entry migrated, a pack's profile layer over a
  starter one; pictures under Xvfb (`FFX_EDITOR_SHOT=1`) (docs/remaster-editor.md,
  "Checks").
- **A game-side check** on the GPU (`--selftest-materialize`, the Windows
  export through `tools/compare-shot-win.sh`; `--no-game`: a synthetic
  texture): the pipeline's time per slider change, per band rebuild and per
  bake at 1024, 2048 and 4096 px, the occlusion settling, and the GPU maps
  against the CPU maps (parity). Phase 2's part of the editor check: a band
  preset changes the height, a picked colour the roughness (its mask
  isolated), the painted light the normal, the occlusion settles in stages,
  the preview's pick finds a UV, the colour check, the save writes a 16-bit
  height; under a renderer the raw texture and the default material lit,
  measured (`--materials-only`: the tab's part alone, for working on it).
  The game-side check's own options: `--materialize-parity-only` (stop after
  the parity), `--materialize-texture=NAME` (another texture, by a part of its
  short name, whatever its alpha and size), `--materialize-size=WxH` (the
  synthetic texture's size, without the game), `--materialize-probe=X,Y[,slot...]`
  (one pixel of passes from both backends), `--materialize-cpu-2048` (the CPU's
  bake at 2048 px too), `--shot=FILE.png` (the GPU's maps side by side, and
  the maps that miss the parity beside it).
- **The materials and lighting checks** (`--selftest-materials`,
  `--selftest-lighting`) with materials made by starter profiles (rock,
  stone and concrete) on the CPU graph.
- **The starter profiles' measure** (`--selftest-materialize
  --materialize-profiles[=DIR] [--materialize-profile-size=N]`): every starter
  profile on its kind's textures, the colour check and the roughness and
  metallic ranges; DIR: contact sheets (keep them out of git);
  `--materialize-atlas=DIR --materialize-ids=FILE`: thumbnails of the listed
  textures, to choose by eye.
- The quick sweep (`tools/sweep-devsaves.sh`) for anything that touches the
  game's drawing.

## Risks

- **Precision in the Compatibility renderer.** The band pyramid and the
  height want float render targets. Measured in phase 2: Compatibility's
  offscreen canvas targets with `use_hdr_2d` are 32-bit floats (Forward+'s
  half floats), so both bake on the GPU.
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
