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
| `effect` | a map's field program (`<level>-fp<entry>`) or a spell overlay (`magic-<id>`, four hex digits): who makes the classic draws (R7a, "Effects" below); or, for the battle's effect records, the record id as eight hex digits (`EffectInstance.id`, `MsGetEffID`: a per-character counter is part of it, so it is not one effect's id from run to run of different battles) | `bsil00-fp15`, `magic-004c`, `50001800` | `res://remake/effect/bsil00-fp15.tscn` |
| `ui` | a Flash movie's art (R7b): the movie (its file name, or without its language) and the art id the screens ask for (a `SymbolClass` name, `#<character>`, `@<label or frame>`) | `saveload` + `saveload_jp_fla.arrow_21`, `saveload` + `saveload_jp_fla.face_565@_1` | `res://remaster/ui/ui.tres` (an image, not a scene: docs/remaster-editor.md, "UI art") |

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

## Retargeting (R6)

A remade character with a skeleton of its own (any bone names, any
proportions, authored in Godot's convention: y up, facing +z, metres) is
driven by the game through a rig (docs/remaster-editor.md, "Characters", makes
one; docs/remaster-plan.md, R6):

- **The scene**: `res://remake/chr/<id>.tscn` (the convention; or a registry
  entry), its root a `RemadeCharacter` (`godot/src/Characters/`) holding the
  model (an instance of your scene) and naming its rig, `<id>.rig.tres`, a
  `CharacterRig`: the alignment (`Align`: your model's space to the game's
  character space: turn, scale, feet), the bones (`RigBone`: bone name, game
  element, body part, `Positions`, the rest offset, the bone it aims at, your
  offset), the cloth chains (`ClothChain`), the face table (`FaceMap`) and the
  attachments (`AttachMap`). A root without a rig keeps G6's path
  (`SkeletonNode_i` bones, above).
- **Each frame** (`Retargeter`, from `StateView3D`): the game's element
  globals (the snapshot's `Actor.bones`, interpolated, in character space) go
  in; per remade bone, parents first: a mapped bone's global rotation is its
  element's times its rest offset, swung so the mapped bone it aims at points
  where the game's element of that bone is (the game's motions move joints as
  well as turn them: Tidus's wrist slides along his forearm), placed at its
  own rest offset from its parent (`Rotation`: your proportions) or on the
  game's joint (`Game`; the hips, so the walk is the game's), then your offset;
  a bone not mapped keeps its rest under its parent. The rig's scale is baked
  into the remade skeleton's rests and skins when the driver is made (the
  root stays unscaled: Godot's spring bones flip on a scaled skeleton).
- **Cloth**, per chain: `Game` turns each bone from where its parent alone
  puts it by how far the game's simulated cloth hangs from its skinned
  direction: the capture's vertices of the chain's segment (PS2 model,
  piece, a vertex per bone: docs/architecture.md, "Cloth"), against their bind
  directions turned by the element the chain hangs from; `Godot` hangs a
  `SpringBoneSimulator3D` on the chain (stiffness, drag, gravity, radius in the
  game's units; it resolves its bones only once under its skeleton, so the
  driver adds it first and configures it after); `Off` leaves it skinned.
- **Face**, per row: a value of a game element's pose relative to another
  (its local rotation about x, y or z in degrees, Euler z y x as the game
  composes, or a position component), mapped from an input range to an output
  range (clamped), onto a blend shape (every mesh of the model that has it)
  or a bone's turn about its own axis. The game's face animation in the
  snapshot is its bones: the jaw (attachment point 13) opens up to 7° as a
  character talks (`Ch_Mouth`), the head turns (`Ch_Neck`, point 9). The eyes'
  and mouths' texture animation (blinks) is not in the game-state API.
- **Attachments**: what the game binds to a character's element (a weapon:
  `Actor.parent`, `parent_bone`) follows the remade character: the bone the rig
  attaches that element to (an `AttachMap`: on a socket bone the bound object
  sits at the socket with the game's grip turn, on a mapped bone it keeps its
  place relative to it), else the remade bone mapped to that element (its
  frame as the element's). Its root moves by that frame's difference from the
  game's element (`StateView3D`). In the deck battle Tidus's sword `w002` is
  bound to element 50 (the right hand's point 5) and sits on the stand-in's
  `Weapon.R` socket; `w066`, bound to element 13 (his left forearm), follows
  the remade forearm.

A remade character is a model of the game's character space like the classic
one: placed under the character's root with half a turn about z.

`--selftest-character --devsave=7` checks it all on Tidus (below).

## Character materials (R6)

Presets for remade characters, original work, in `res://remake/materials/`
(`CharacterMaterials` gives copies), their shaders in `res://remake/shaders/`:

| Preset | What |
|---|---|
| `skin.tres` | `StandardMaterial3D` with Godot's subsurface scattering in skin mode and transmittance (Forward+; the Compatibility renderer draws it without) |
| `hair.tres` | `hair.gdshader`: strands as alpha (stripes along the strand direction, each of its own length), hashed alpha, anisotropic highlights along the strands (Godot's `ANISOTROPY` and `ANISOTROPY_FLOW`, from a flow direction or a flow map), root and tip colours, rim |
| `hair_a2c.tres` | the same with alpha to coverage (MSAA smooths the strand edges) |
| `eye.tres` | `eye.gdshader`: a latitude-longitude sphere looking along +z; the iris looked up along the view ray refracted by the cornea (a parallax: it shifts against the cornea as the view turns), pupil, limbal ring, a wet clear coat with a sharp highlight |
| `cloth.tres` | rough two-sided fabric with a rim |

Godot's subsurface scattering scale is a project setting in metres
(`rendering/environment/subsurface_scattering/subsurface_scattering_scale`);
characters in the game's units are about ten times larger, so a remaster
project may raise it.

## The stand-in humanoid (R6)

`StandInHumanoid` (`godot/src/Characters/StandInHumanoid.cs`) makes an
original test character in code, as a user's glTF would come: y up, facing
+z, 1.8 m, a T pose, a skeleton of its own unlike the game's (names such as
`Pelvis`, `Spine_Lower`, `UpperArm.L`, `Calf.R`; longer legs, a shorter torso,
three spine bones, a jaw, eyes, a weapon socket per hand), skinned tubes and
spheres with the skin, hair, eye and cloth presets, a head with the blend
shapes `mouth_open` and `blink`, a hood down the back (`Hood_1..3`) and a
ponytail (`Tail_1..3`). Nothing of it is the game's; `StandInHumanoid.Save`
writes it as a scene. The checks make it at run time (no file in git).

## Effects (R7a)

The game's effects have no ids of their own in its draws (a draw's key holds
addresses); gd-ffx gives each the id of what makes it (the extension's
`fxowner.rs`, from the game's memory at the update the frame drew; ffx-rust's
docs/formats/effects-draw.md, "Instance keys and makers", and scene-maps.md,
"Map effects: the field-program table"):

- **`<level>-fp<n>`**: entry `n` of the map's field-program table, a map's own
  particle effect (Besaid beach: `bsil00-fp0`..`fp13` the sea's foam and wave
  strips, `bsil00-fp15` the save sphere's glow). The ppp objects that draw it
  are the entry's part's (`g_FpRun + 0x80 n`, its list from `+0x30`).
- **`magic-<id>`**: the spell overlay `magic_<id>.dll` (`magic-004c` Fire): the
  draws whose object or birth record lies inside the loaded overlay's image.

Both are stable: the table is the map's file, the overlay the spell's. Other
draws (the battle's shared effects, the op system's records) have none yet and
stay classic.

A remade effect is a scene for the id (kind `effect`: the convention's
`res://remake/effect/<id>.tscn`, a `remake.json` entry, a pack's), authored in
render space (the game's units, y up; the save sphere's orb is about 2.5
units across). While the classic effect draws, the scene is instanced at its
place (a field program's table entry, or the centre of the effect's draws for
one its program places itself or one stuck to a character, and for spells),
following it, and the classic effect's draws are left out. When the classic
effect stops (no draw for 0.25 s) the remake's particles stop emitting and it
is freed once they have died out. Its root may be a `RemadeEffect`
(`godot/src/Remake/RemadeEffect.cs`) with options: `HideClassic` (off: both
drawn, for comparing), `Follow` (off: stays where it started), `Offset`
(render units), `Linger` (seconds after the end; 0: the longest particle
lifetime), `Note`. F8 in the game turns remakes off and on for the run.

The battle's effect records (`Effects.running`) keep G6's path (`EffectView`):
a scene registered for a record id is instanced on its targets while the game
keeps the record (the classic draws are not left out there).

`res://remake/examples/save_sphere_sparkle.tscn` is R7a's example: rising
sparkles and a soft light, made of Godot's particles and generated gradients
(original work, nothing of the game's). The checks register it for
`bsil00-fp15` (`--selftest-effects`).

## Replaced sounds and movies (R7a)

Music, sound effects, voice lines and movies are replaced by the ids the game
plays them by (docs/remaster-editor.md, "Effects, audio and movies"):
`RemasterSoundSet` (`res://remaster/audio/audio.tres`: per entry the kind,
id, file, loop, loop points, volume) and `RemasterMovieSet`
(`res://remaster/movies/movies.tres`: per entry the movie's name, video,
sound, subtitles, volume), each a layer of its own over the packs'
(`res://mods/<pack>/remaster/audio/audio.tres`, `.../movies/movies.tres`; a
later layer wins per id, `Classic` pins an id to the game's). The files they
name are yours (Ogg Vorbis, WAV, MP3; Ogg Theora); keep them in your pack.

## Rebuilt 2D rooms (R7b)

A map the game draws as painted layers (docs/architecture.md, "The 2D layers")
is remade like any map: a scene for its level (`res://remake/map/ptkl14.tscn`,
a `remake.json` entry, a pack's), in render space. The editor's **Rebuild in
3D** (docs/remaster-editor.md) makes one whose root is a `RebuiltRoom`
(`godot/src/Remaster/RebuiltRoom.cs`): a `RoomProxy` for each layer (by 2D
scene and layer id) and for the floor, a `Model` group for the user's geometry,
nothing of the game's in the file. When the game shows the level it builds the
room from the layers it loaded anyway (their pictures from the player's
archive), the map's game data (the scene cameras, the walk mesh: `MapGameData`),
the camera's far plane and the map's own term of the 2D window (`map_pan`, 0 on
the maps measured):

- each layer a plane over its pixel rectangle at view distance 10 z from its 2D
  scene's fixed camera, with its picture projected from that camera
  (`ClassicMaterials.ProjectedLayerShader`: the layer shader's colour, blend,
  depth write, cut and order), so from the game's camera the plane draws the
  layer pixel for pixel and the characters go behind nearer layers as they do
  classic; any mesh can take a layer's projection (a proxy's own mesh, or the
  metadata `ffx_layer` = `"scene:layer"` on a mesh of the user's);
- the floor the walk mesh's walkable triangles, the farthest layer projected,
  drawn first without depth (the layers cover it from the game's camera); the
  game walks the characters on the walk mesh as on any map, so they stand on it;
- the proxies follow what the game shows: the 2D scene (`PhyreScene_change2DSceneNum`)
  and the layers' shown flags (`PhyreScene_show2DLayer`), as `MapLayers` does.

The rebuilt room is drawn instead of the classic layers unless the render path
is classic; F7 on a rebuilt room switches between it and the classic layers
(`classic`, then `map`). The camera of these rooms is the game's fixed camera
and stays it: the game has no other view of them, so a rebuilt room is seen
from where the painting was (a model that looks right from there; another view
would show what the painting hid). The checks: `--selftest-rebuild` (developer
saves 14, 12, 52; docs/remaster-editor.md, "The rooms and UI art checks in the
game"), the editor check's step 8.

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

A pack's remaster textures and materials (docs/remaster-editor.md,
"Textures") are in `res://mods/<pack>/remaster/textures/textures.tres` and
layer over the project's per texture ID, the later pack winning; its replaced
sounds and movies in `res://mods/<pack>/remaster/audio/audio.tres` and
`res://mods/<pack>/remaster/movies/movies.tres`, per id the same way.

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
remaster plugin writes them: docs/remaster-editor.md; or by hand) and
diffable in git. They hold IDs and original work only, never the
game's content.

### The files

Per map, keyed by its level (`bsil00`):

- `res://remaster/maps/<level>/overrides.tres`: a `MapOverrides` resource
  (`godot/src/Remaster/MapOverrides.cs`);
- `res://remaster/maps/<level>/additions.tscn` (optional): nodes to add
  (or the resource's `Additions` scene);
- `res://remaster/maps/<level>/lighting.tres` (optional, R3): the map's
  remastered lighting, a `MapLighting` (docs/remaster-editor.md, "Lighting");
  it layers per file (the last layer's wins), not per field.

`MapOverrides`: `Level` (informational: the folder decides), `Status`
(`Original`, `InProgress`, `Remastered`, `Approved`), `RenderPath`
(`Classic`, `Remastered`: how the map is drawn, docs/remaster-editor.md, "The render path"), `Notes`, `Parts` (an array of
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

`res://remake/examples/stand_in_effect.tscn` is a remade battle effect record (a ring):
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
# R6: the stand-in humanoid retargeted on Tidus, walking (bone directions and joints, cloth, face, materials)
FFX_GAME_DIR=... godot --headless --path . -- --selftest-character --devsave=7
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

`--selftest-character` boots to Besaid beach, builds the stand-in's rig
against the classic `c001` (the automatic mapping, alignment, rest offsets,
cloth, face and attachments; the hierarchy alone, with the stand-in's names
hidden, must find the same body parts), saves it (`--character-dir=DIR`, else
`user://r6-check`; or takes the editor's with `--character-scene=FILE`),
registers it for `c001`, has the view take it (`StateView3D.Reload`) and
walks the player left for 3 s, then right for 3 s. It checks, read back from
the remade skeleton: with the remade proportions, every mapped bone's
direction to the bone it aims at within 3° of the game's (measured: 0.09°
at most, mean 0.013°); with every mapped bone on the game's joint, each joint
within 0.05 units (0.0000); the remade hips walking (48 units); the hood
turned by the game's cloth (Tidus's segment model 0, piece 0; up to 59° from
its skinned pose, mean 30°, moving every frame), the ponytail moved by Godot's
spring bones (read after Godot's modifiers ran) and stable: 95 % of the frames
under 60° from its rest (measured: median 27°, 95 % under 34°, swinging 25° to
33° with the walk, about 50° as Tidus turns about, 71° once); the face table driven every frame (the blend shape set to the row's
output; on the beach Tidus does not talk, so the jaw stays shut) and a jaw
opened 5° by hand opening `mouth_open` to 0.71; the material presets loaded.
With `--battle=3,0,0` it runs in the deck battle instead of walking: the
directions as above (0.08°), and every object the game binds to Tidus
following the remade character (the sword's root on the `Weapon.R` socket,
0.00 units). With `--shot=FILE.png` under a renderer: `FILE.png` (classic
map), `FILE-remastered.png`, `FILE-closeup.png` (the camera at the remade
character).

