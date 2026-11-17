# The remaster editor

gd-ffx's editor plugin for the remaster (docs/remaster-plan.md, R1b):
browse the game's maps, open one as an editable scene of its classic parts,
move, hide, remove, replace and add parts with Godot's own tools, and save
the edits as the map's override files (docs/remake.md, "Overrides"), which
the game applies. Everything it writes is IDs and your own work, never the
game's content.

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
  (Classic, Remastered: used from R2) and the notes are written into the
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
  map is shown as the file places it: objects the game moves at run time
  (Besaid's birds) are at their file place; their edits are relative to
  the game's placement in the game.
- The map's existing overrides show as edits: removed parts are out of the
  scene (the Parts list keeps them, for Revert), hidden ones hidden, moved
  ones moved, replaced ones drawn by their replacement, added nodes under
  their anchors.
- Maps with 2D layers only cannot be opened (nothing 3D to edit yet).

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

## Checks

```bash
FFX_GAME_DIR=<game> tools/remaster-editor-check.sh [OUT_DIR]                    # headless
FFX_EDITOR_SHOT=1 FFX_GAME_DIR=<game> tools/remaster-editor-check.sh [OUT_DIR]  # under Xvfb, with pictures
```

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
3. The round trip (`--remaster-editor-roundtrip`, DIR holding R1a's
   example): opened, each entry shown as its edit and both additions at
   their anchors; saved without an edit, the same entries and additions.

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
- 2D-layer maps (the painted rooms) and remade maps' own scenes are not
  opened (the editor always shows the classic import; overrides on a remade
  map's tagged nodes are made by hand).
- Script-moved objects are shown at their file place, not where the game
  puts them, and morphs are not played (R5 brings the game's data into the
  editor).
- The render path is stored only (R2).
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
| `EditorSelfTest.cs` | the check |

Game side: `GameHost.ClassicGame` (the running game's object, else the
editor's), `ClassicLibrary.Load` in the editor (the glTF images kept in
memory; Godot's importer mesh instances made mesh instances, as the game has
them), `MapOverrideRuntime.SplitSurface` (one segment as a node, shared with
the game), `SavedOverridesTest` (`--selftest-saved-overrides`).
