# Remade content and mod packs (G6 groundwork)

Everything gd-ffx shows has an id, the game's own. A remade scene registered
for an id takes over from the classic import (docs/importer.md); anything
without a remake stays classic. Remade and classic content sit side by side:
a remade character on a classic map, or the other way round.

## Ids

| Kind | Id | Example | Convention file |
|---|---|---|---|
| `map` | the level the game's renderer loaded (`World.level`), or its last part | `/map/bsil/bsil00/` or `bsil00` | `res://remake/map/bsil00.tscn` |
| `chr` | the model name (`Actor.model`) | `c001` (Tidus) | `res://remake/chr/c001.tscn` |
| `effect` | the effect id as eight hex digits (`EffectInstance.id`, `MsGetEffID`) | `50001800` | `res://remake/effect/50001800.tscn` |
| `ui` | later | | |

A map's level and its last part are the same id (`bsil00`). The overlay
(F12) shows the level and each character's model name; `ffx-import devsaves`
lists the developers' saves with their maps.

## Registering

The registry (`godot/src/Remake/RemakeRegistry.cs`) is filled at start-up, in
this order, a later entry over an earlier one:

1. `res://remake/remake.json`, the base registry (absent for now);
2. each mod pack's `res://mods/<pack>/remake.json`, in load order (below);
3. `RemakeRegistry.Register(kind, id, scene)` at run time.

A registry file maps kinds to ids to scenes:

```json
{
  "chr": { "c001": "res://remake/tidus/tidus.tscn" },
  "map": { "/map/bsil/bsil00/": "res://remake/besaid/beach.tscn" }
}
```

A scene of `""` or `null` pins the id to classic (a pack can undo another
pack's replacement). Without an explicit entry the convention applies: a
scene at `res://remake/<kind>/<id>.tscn` (or `.scn`) is used. An explicit
entry wins over the convention, whatever the order.

`ContentResolver.Resolve(classic, kind, id)` (`godot/src/Remake/ContentResolver.cs`)
is the one place that chooses: the remade scene if one is registered, else
the classic import (asked for when needed). A remade entry that does not
load (not a scene, or a root that is not a `Node3D`) is logged once and the
id falls back to classic. A remade scene comes wrapped in a node of its
own: the view sets the wrapper's transform (a character's turn, a map's
scale) and the scene's root keeps its own. Every instance carries the
metadata `ffx_kind`, `ffx_id`, `ffx_source` (`remade` or `classic`) and
`ffx_path`. `StateView3D` takes maps and characters from it.

## Spaces

**A remade map** is authored in the game's render space, which is Godot's
(right-handed, y up), in the game's render units, and placed at the origin
of the 3D view at scale 1: the camera, the characters' roots and the game's
positions are all in that space. Classic map files are a tenth of it (the
view scales them by 10, `ClassicLibrary.MapScale`); a remade map is not
scaled, so one render unit is one unit of the scene. To line one up,
open the classic import (`<classic cache>/map/<map>.glb`, made from your own
copy) scaled by 10 as a reference, and leave it out of the scene you save.
The game does the walking (its own collision); a remade map only draws.

**A remade character** is placed as the classic model is: under the
character's root (its position and turn from the game) with half a turn
about z (on the wrapper: the scene's root keeps its own transform), so it
is authored in the game's character space: y down, feet at
y = 0, the head towards -y (Tidus is about 18 units tall), facing as the
classic model does (compare with `<classic cache>/chr/<model>.glb`).

## Posing a remade character

The game poses every character's skeleton each frame (interpolated to the
display's rate). A remade character gets the same:

- a `Skeleton3D` anywhere under the scene's root (the first found);
- bones named `SkeletonNode_<i>`: the game's element `i` (`Actor.bones[i]`,
  a matrix relative to the character) becomes that bone's global pose, in
  character space as above. The classic model of the same id has the same
  names (look at its glb for which elements it uses; Tidus has 136);
- elements the game leaves at the identity keep the bone's own pose; bones
  with other names are not touched. The global poses are set in the
  skeleton's order, parents before children (by depth), whatever the
  element numbers, so any hierarchy works, a flat one too. Meshes skinned
  to the skeleton or hung on `BoneAttachment3D`s follow.

A scene that animates itself instead (an `AnimationPlayer`, a physics rig)
opts out with the metadata `ffx_posed` = `false` on its root: it is still
placed, turned, shown and hidden by the game, but its skeleton is left alone.

## Mod packs

Godot packs (`.pck`) carry remade content: scenes, meshes, textures,
materials, GDScript. When the launcher hands over (docs/launcher.md; at
once when it is skipped), before `scenes/main.tscn` loads, `GameHost.Start`
has the `ModLoader` autoload mount (`ModLoader.MountAll`):

1. every enabled `*.pck` in `user://mods/` (Godot's user data folder for gd-ffx:
   `~/.local/share/godot/app_userdata/gd-ffx/mods/` on Linux,
   `%APPDATA%\Godot\app_userdata\gd-ffx\mods\` on Windows);
2. then every enabled `*.pck` in each `--mods=DIR` (after `--`; repeatable).

Each folder's load order: the packs named in its `load_order.txt` (one file
name a line, `.pck` optional, any case, `#` comments), in that order, then the others
alphabetically. A line `# disabled: NAME` keeps a pack at its place but
does not mount it (the launcher writes these; to anything else they are
comments). Later packs win: a file at the same `res://` path replaces
the earlier one (so a pack can ship `res://remake/chr/c001.tscn` and win the
convention), and its registry entries come later. A pack named
`<pack>.pck` registers ids with `res://mods/<pack>/remake.json`; keep a
pack's own files under `res://mods/<pack>/` too, so packs do not collide by
accident. A pack's `res://remake/remake.json` has no effect (the base is
read before the packs).

**Install only packs you trust.** A pack can replace any `res://` file,
gd-ffx's own scenes included, not only remake content, and it can carry
GDScript, which runs with gd-ffx's rights (`OS.execute`, reading and
writing your files). Mounting does not check what a pack contains; a pack
that keeps to `res://mods/<pack>/` and `res://remake/` is the convention,
not a guarantee.

`--no-mods` mounts none. The log says what was mounted:

```
ffx: mods: loaded tidus-hd (.../mods/tidus-hd.pck), 1 remake entries
ffx: mods: 1 pack(s): tidus-hd
```

The launcher (docs/launcher.md) lists the packs of each folder with a
switch and Up and Down, and writes the folder's `load_order.txt`.

Not yet: C# scripts in packs (they need gd-ffx's assembly rebuilt; use
GDScript or plain scenes), `ffx-rust`'s data mods (and the launcher
listing them beside the packs).

## Making a pack

A mod project is a Godot project with the same `res://` paths. Put the
scenes under `res://mods/<pack>/` (or at the convention's
`res://remake/<kind>/<id>.tscn`), the fragment at
`res://mods/<pack>/remake.json`, and export only those files as a pack:
Project → Export → a preset with "Export selected resources" →
"Export PCK/ZIP...", or headless
`godot --headless --path <project> --export-pack "<preset>" <pack>.pck`.
`PCKPacker` (in a script) builds one without an export preset, as the self
test does. Never put files of the game or the classic cache into a pack.

## Overrides (R1)

A remade scene replaces a whole map. Overrides edit a map part by part:
the classic import, keyed by its stable part IDs (docs/importer.md,
"Stable part IDs"), or a remade map's nodes tagged with `ffx_part` the same
way. They are Godot resources and scenes, made in Godot's editor (the
remaster plugin, docs/remaster-plan.md R1b, writes them; by hand for now)
and diffable in git. They hold IDs and original work only, never the
game's content.

### The files

Per map, keyed by its level (`bsil00`):

- `res://remaster/maps/<level>/overrides.tres`: a `MapOverrides` resource
  (`godot/src/Remaster/MapOverrides.cs`);
- `res://remaster/maps/<level>/additions.tscn` (optional): nodes to add
  (or the resource's `Additions` scene).

`MapOverrides`: `Level` (informational: the folder decides), `Status`
(`Original`, `InProgress`, `Remastered`, `Approved`), `RenderPath`
(`Classic`, `Remastered`: read by R2), `Notes`, `Parts` (an array of
`PartOverride`), `Additions`. A `PartOverride` (`PartOverride.cs`) edits one
part by `Id`; every operation has an `Unset`, so a layer says only what it
changes:

| Field | Values | Effect |
|---|---|---|
| `Id` | part ID | `#<node>/*` an instance, `#<node>/<k>` one segment, `#<node>` a node (relative to the map's classic 3D model), a full `x:/...` ID, or an added node's `x:level/<level>/+<layer>/<name>` |
| `Remove` | `Remove`, `Keep` | the part is not drawn, nor anything under it; `Keep` undoes an earlier layer's removal |
| `Visibility` | `Hidden`, `Shown` | hidden whatever the game says; `Shown` undoes an earlier layer's hiding (the game still shows and hides it) |
| `TransformMode` + `Transform` | `Offset`, `Absolute`, `Game` | `Offset`: on top of the game's placement each frame, `W' = (B W.basis, W.origin + t)` (the transform's basis turns and scales about the part's origin in world axes; its origin moves it, in render units); `Absolute`: the world transform in render space; `Game` undoes an earlier layer's |
| `Replace` + `Scene` | `Scene`, `Original` | the part is not drawn; the scene is, in the part's frame (its origin and axes, unscaled: render units), following the game's placement and visibility of the part; `Original` undoes an earlier layer's |
| `Note` | text | why, what next |

An added node is each child of the additions scene's root. Its metadata
`ffx_anchor` (a part ID) puts it under that part, at the part's origin and
axes in render units, following it (a bird the game flies); without one it
sits under the map's root in render space. Its ID is
`x:level/<level>/+<layer>/<name>` (the layer below), which a later layer
can edit or remove like any part.

Most classic map instances are placed by the file at the origin, their
vertices already where they stand: their frame is render space, so a
replacement or an added node is authored where the part is (open the
classic import scaled by 10 as a reference, "Spaces" above). Script-moved
objects (Besaid's birds) are placed by the game each frame and have their
own frame.

```
[gd_resource type="Resource" script_class="MapOverrides" load_steps=4 format=3]

[ext_resource type="Script" path="res://src/Remaster/MapOverrides.cs" id="1"]
[ext_resource type="Script" path="res://src/Remaster/PartOverride.cs" id="2"]

[sub_resource type="Resource" id="up"]
script = ExtResource("2")
Id = "#FBXASC0492_0/*"
TransformMode = 1
Transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 20, 0)

[resource]
script = ExtResource("1")
Level = "bsil00"
Status = 1
Parts = Array[ExtResource("2")]([SubResource("up")])
```

### Layers

A map's overrides come from, in order:

1. `res://remaster/maps/<level>/overrides.tres`, the project's (layer `base`);
2. each mounted pack's `res://mods/<pack>/remaster/maps/<level>/overrides.tres`,
   in load order ("Mod packs" above; layer = the pack's name), with its
   `additions.tscn` beside it;
3. files registered at run time (`RemasterOverrides.Register`; the checks).

A later layer wins per part ID and per field: a pack can move a part
another pack hid, and it stays hidden; a `Keep`, `Shown`, `Game` or
`Original` undoes an earlier layer's edit. Every layer's added nodes are
added. The status, render path and notes are the last layer's.
`--no-overrides` turns them all off (the "before").
`godot/src/Remaster/RemasterOverrides.cs` merges them, once per level
(`Invalidate` to read again).

### In the game

`ContentResolver` applies a map's overrides when it builds the map's scene
(`MapOverrideRuntime.Apply`): a classic map's on the loading worker, before
the view's object index (maps loaded ahead and parked ones keep theirs),
merged on the main thread when the load starts; a remade map's on the main
thread. Nothing is freed, so the game's object index, the morphs and the 2D
layers stay valid:

- a removed part is hidden for good and its surfaces draw nothing (a
  material whose vertices fall outside the clip volume: no pixels, no
  shadow), the node stays;
- a part ID of one segment (`#<node>/<k>`) with an edit is first split off:
  the surface becomes a child `MeshInstance3D` of its instance (its arrays,
  morph targets, materials; the object id kept, the instance number not, so
  the game's object index skips it and its morph is driven as the
  instance's), and the instance's surface draws nothing. The split part
  follows its instance;
- each frame `StateView3D` puts the edits on top of the game's placement of
  its script-moved objects (`SceneObject` matrices, interpolated) and keeps
  hidden parts hidden; the parts the game does not place keep their edits
  relative to their parent; the holders of replacing and added nodes stay
  at their anchor's origin and axes, unscaled.

The log says what was applied, and every ID the map lacks:

```
ffx: overrides: bsil00: 5 part(s) over 1 layer(s): selftest (res://remaster/examples/bsil00/overrides.tres)
ffx: overrides: bsil00 applied: 1 removed, 1 hidden, 2 moved, 1 replaced, 2 added (2 part(s) split off)
```

### Status

The map catalogue's entry (docs/importer.md, "The map catalogue";
`MapEntry.State()`) says per map whether it has overrides (any layer) and a
remade scene (derived), its hand-set status, render path and notes (its
overrides' last layer), and `Effective`: the hand-set status, but at least
`InProgress` when the map has overrides or a remade scene.
`RemasterOverrides.Levels()` lists the levels with overrides.

## Example and tests

`res://remake/examples/stand_in_character.tscn` is a stand-in made of
Godot's shapes: a capsule 16 units tall in character space and a flat
skeleton of `SkeletonNode_0..23` with a dot on each bone. It is for the
tests and as a template; it is not registered for any id.

`res://remake/examples/stand_in_effect.tscn` is a remade effect (a ring):
an effect scene is instanced at each of the effect's targets (or its
character) when the game starts the effect record and freed when the game
ends it. The battle check registers it for `50001800`, the first effect of
the deck's battle.

`res://remake/examples/stand_in_map.tscn` is a remade Besaid beach (level
`/map/bsil/bsil00/`) made of Godot's shapes only: a sand plane, the sea at
the game's water level (render y -3.5; docs/formats/scene.md in ffx-rust,
"The water level") where the sand runs under it, palms and a sky dome. It
is in render space, unscaled. The sand is the plane `y = -0.0422 x - 0.0690
z - 10`, fitted to where the game stands Besaid's characters (within 2.6
units), so the game's walking (its own collision) keeps them on it. A real
remade map does the same: it follows the ground the game walks on, which
the classic import shows.

`res://remaster/examples/bsil00/` is an override set for Besaid beach
(Godot's shapes and part IDs only): it removes an instance
(`#FBXASC0494_1/*`), hides one segment (`#polySurface32/0`, the far sea:
split off), moves an instance up 20 units (`#FBXASC0492_0/*`) and a bird
the game flies 40 (`#FBXASC0498_0Shape/*`), replaces a hut's thatch
(`#polySurface4/0`) by a box (`replacement.tscn`), and adds a sphere above
a hut and one on another bird (`additions.tscn`). It is outside
`res://remaster/maps/`, so only the check registers it.

```bash
cd godot
# packs: load order, overrides, the convention, instantiation (no game needed)
godot --headless --path . -- --selftest-mods --no-game
# Tidus drawn by the stand-in and posed by the game on Besaid's classic map
FFX_GAME_DIR=... godot --headless --path . -- --selftest-remake --devsave=7
# Besaid beach drawn by the remade example; the player walks over it off the beach
FFX_GAME_DIR=... godot --headless --path . -- --selftest-remake-map --devsave=7
# the example overrides on Besaid beach (R1)
FFX_GAME_DIR=... godot --headless --path . -- --selftest-overrides --devsave=7
# its pictures, after and before (offscreen, Xvfb)
FFX_GAME_DIR=... FFX_SHOT_TEST="--selftest-overrides --selftest-timeout=90" ../tools/compare-shot.sh - /tmp/ov-after
FFX_GAME_DIR=... FFX_SHOT_TEST="--selftest-overrides --selftest-timeout=90 --no-overrides" ../tools/compare-shot.sh - /tmp/ov-before
```

`--selftest-mods` reads load orders with disabled packs (and writes one
back), builds four packs at run time with `PCKPacker` in a
temporary folder (from the example), mounts them through the same code as
`--mods=DIR` and checks the order (names in any case), that the disabled one is not
mounted, a file in two packs,
registry overrides, an id pinned to classic, the convention from a pack,
that the scenes instantiate with their skeleton and their root's own
transform, that a non-scene entry and a scene whose root is not a `Node3D`
fall back to classic, and that a registry file with a bad entry registers
nothing. `--selftest-remake` registers the stand-in
for `c001` at run time, boots to Besaid beach and checks that the player is
drawn by it (no classic `c001` beside it), that the game poses its bones and
keeps posing them, and that the map is still the classic one.
`--selftest-remake-map` registers the remade beach for its level at run
time, boots there and checks that the remade map is drawn (not the classic
one, unscaled), that the player stands within 4 units of its sand, and
walks the player off the beach to the next map as `--selftest-besaid` does
(with `--shot=FILE.png` under a renderer, a picture first).
`--selftest-overrides` registers the example overrides for Besaid beach,
boots there and checks each edit in the running scene: the removed
instance is hidden, draws nothing and is still the game's object; the
hidden segment is split off and hidden while its instance's other surfaces
still draw; the moved instance is the game's placement plus its offset;
the replacement sits unscaled in its part's frame; the added nodes are at
their anchors. Over two seconds the game flies the moved bird, which keeps
its offset and goes on morphing, and the sphere added on the other bird
follows it. Then the catalogue's entry: Besaid beach's place in English
and Japanese, with overrides, status `InProgress`; another map without.
`--selftest-mods` also layers overrides from two packs (per part and per
field, the later pack winning). `--overrides-survey` lists the beach's
instances on screen and the ones the game moves (for picking parts).
