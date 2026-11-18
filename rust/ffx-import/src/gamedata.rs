//! A map's game data for the remaster editor (gd-ffx's docs/remaster-editor.md,
//! "The game's data"): what the game walks, triggers and looks through on a
//! map, read from the player's archive the way the game's loaders read it,
//! without running the game (ffx-rust's docs/formats/field.md, script.md,
//! scene-maps.md, chara.md and main.md describe the formats):
//!
//! - **The walk mesh** (`MAP1` sections 2 to 8, pages 0 to 6 of
//!   `yiSetIDPage`; field.md, "The walk mesh on disk"): the triangles
//!   characters stand on, with each triangle's map id word (its kind, bits
//!   0-6, decides who may enter it: `Sg_IdMatchChr`; main.md, round 4).
//! - **The scenes' cameras** (`MAP1` section 0, the `YNSC` chunks'
//!   camera blocks: eye, look-at, up, field of view, y offset;
//!   scene-maps.md, "Map cameras in the files"), the fixed cameras of the
//!   2D-layer maps.
//! - **The event script**: its entrances (`0x20` bytes each: map index,
//!   camera, yaw, position; script.md, "Script block") and, per worker, its
//!   type, what its init entry sets with constant arguments (trigger lines
//!   and boxes, positions, the model, the map group or display object it
//!   binds), and the maps it changes to (the map change natives with
//!   constant arguments, as [`crate::exits`]).
//!
//! Positions are in field units (y down); gd-ffx's render space is `(-x,
//! -y, z)`.

use ffx_script::opcode::{OPCODES, Operand};
use serde_json::{Value, json};

fn u16_at(b: &[u8], o: usize) -> Option<u16> {
    b.get(o..o + 2).map(|s| u16::from_le_bytes([s[0], s[1]]))
}

fn i16_at(b: &[u8], o: usize) -> Option<i16> {
    u16_at(b, o).map(u16::cast_signed)
}

fn u32_at(b: &[u8], o: usize) -> Option<u32> {
    b.get(o..o + 4)
        .map(|s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
}

fn f32_at(b: &[u8], o: usize) -> Option<f32> {
    u32_at(b, o).map(f32::from_bits)
}

fn vec3_at(b: &[u8], o: usize) -> Option<[f32; 3]> {
    Some([f32_at(b, o)?, f32_at(b, o + 4)?, f32_at(b, o + 8)?])
}

/// The sections of a `MAP1` file (`yiMapGetSection` `0x00907f00`: the
/// offsets at `+0x10 + 4 * index`, relative to the file; 0 for none), each
/// up to the next section or the end. Empty for another file.
#[must_use]
pub fn map1_sections(file: &[u8]) -> Vec<Option<&[u8]>> {
    if !file.starts_with(b"MAP1") {
        return Vec::new();
    }
    let mut offsets = Vec::new();
    let mut o = 0x10;
    // the table ends where the first section begins
    let mut first = file.len();
    while o + 4 <= first && offsets.len() < 32 {
        let v = u32_at(file, o).map_or(0, |v| v as usize);
        if v != 0 && v < file.len() {
            first = first.min(v);
        }
        offsets.push(if v < file.len() { v } else { 0 });
        o += 4;
    }
    offsets
        .iter()
        .map(|&s| {
            (s != 0).then(|| {
                let end = offsets
                    .iter()
                    .copied()
                    .filter(|&e| e > s)
                    .min()
                    .unwrap_or(file.len());
                &file[s..end]
            })
        })
        .collect()
}

/// One walk-mesh triangle: its vertices, its neighbours across the edges
/// 0-1, 1-2 and 2-0 (-1: none) and its map id word (chara.md, "The map id
/// word").
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Triangle {
    pub vertices: [u16; 3],
    pub neighbours: [i16; 3],
    pub word: u32,
}

impl Triangle {
    /// The kind (bits 0-6): 0 open, 1 closed to everyone, 2 to all but the
    /// player, 13 to chocobos, 14 to the player; `0x30..0x3f` as a table
    /// the scripts set says (`Sg_IdMatchChr`).
    #[must_use]
    pub fn kind(&self) -> u32 {
        self.word & 0x7f
    }
}

/// A walk-mesh page (field.md, "The walk mesh on disk").
#[derive(Debug, Clone, PartialEq)]
pub struct WalkMesh {
    /// The page (`yiSetIDPage`): `MAP1` section `page + 2`; 7 for section 9.
    pub page: u8,
    pub version: i16,
    /// The file's scale (`+0x0c`, the scale times 10): a vertex over
    /// `scale / 10` is in field units.
    pub scale: f32,
    /// The vertices in field units.
    pub vertices: Vec<[f32; 3]>,
    pub triangles: Vec<Triangle>,
}

/// A walk-mesh page from its `MAP1` section; `None` when it is not one.
#[must_use]
pub fn walk_mesh(page: u8, sec: &[u8]) -> Option<WalkMesh> {
    let version = i16_at(sec, 4)?;
    let groups = i16_at(sec, 6)?;
    let count = usize::try_from(i16_at(sec, 0xa)?).ok()?;
    let scale = f32_at(sec, 0xc)?;
    if groups < 1 || !scale.is_finite() || scale == 0.0 {
        return None;
    }
    let verts = u32_at(sec, 0x18)? as usize;
    let table = u32_at(sec, 0x1c)? as usize;
    let tris = usize::try_from(i16_at(sec, table + 8)?).ok()?;
    let tri_at = u32_at(sec, table + 0xc)? as usize;
    let unit = scale / 10.0;
    let vertices = (0..count)
        .map(|i| {
            let o = verts + 8 * i;
            Some([
                f32::from(i16_at(sec, o)?) / unit,
                f32::from(i16_at(sec, o + 2)?) / unit,
                f32::from(i16_at(sec, o + 4)?) / unit,
            ])
        })
        .collect::<Option<Vec<_>>>()?;
    let triangles = (0..tris)
        .map(|i| {
            let o = tri_at + 16 * i;
            Some(Triangle {
                vertices: [u16_at(sec, o)?, u16_at(sec, o + 2)?, u16_at(sec, o + 4)?],
                neighbours: [
                    i16_at(sec, o + 6)?,
                    i16_at(sec, o + 8)?,
                    i16_at(sec, o + 10)?,
                ],
                word: u32_at(sec, o + 12)?,
            })
        })
        .collect::<Option<Vec<_>>>()?;
    if triangles
        .iter()
        .any(|t| t.vertices.iter().any(|&v| usize::from(v) >= count))
    {
        return None;
    }
    Some(WalkMesh {
        page,
        version,
        scale,
        vertices,
        triangles,
    })
}

/// A `YNSC` scene's camera block (scene-maps.md, "Map cameras in the
/// files"): field units before the scene's scale of 10.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SceneCamera {
    /// The scene's number in the background file (its `YNSC` chunks in order).
    pub scene: usize,
    /// Its layers.
    pub layers: i32,
    pub eye: [f32; 3],
    pub target: [f32; 3],
    pub up: [f32; 3],
    /// Degrees; 0: the block sets nothing.
    pub fov: f32,
    pub y_offset: i32,
}

/// A transfer (`rcbgTrxLoad`): 24 bytes, the entry count and size, the entries.
fn skip_transfer(b: &[u8], at: usize) -> Option<usize> {
    let n = u32_at(b, at + 0x18)? as usize;
    let size = u32_at(b, at + 0x1c)? as usize;
    let end = at.checked_add(0x20)?.checked_add(n.checked_mul(size)?)?;
    (end <= b.len()).then_some(end)
}

/// A `YNSC` chunk's body (`rcbgSceneLoad` `0x0092b5e0`): the layer count,
/// 12 bytes, the layers (`rcbgLayerLoad`: a `0x90`-byte record, the frame
/// count and 12 bytes, the frames padded to 16 and their transfer, 16
/// bytes, the image transfer), then the camera block.
fn scene_camera(scene: usize, b: &[u8]) -> Option<SceneCamera> {
    let layers = u32_at(b, 0)?.cast_signed();
    if !(0..=256).contains(&layers) {
        return None;
    }
    let mut at = 0x10;
    for _ in 0..layers {
        at += 0x90;
        let frames = u32_at(b, at)?.cast_signed();
        at += 0x10;
        if frames > 0 {
            let n = usize::try_from(frames).ok()?;
            at += (n * 4).div_ceil(16) * 16;
            at = skip_transfer(b, at)?;
        }
        at += 0x10;
        at = skip_transfer(b, at)?;
    }
    Some(SceneCamera {
        scene,
        layers,
        eye: vec3_at(b, at)?,
        target: vec3_at(b, at + 0x10)?,
        up: vec3_at(b, at + 0x20)?,
        fov: f32_at(b, at + 0x30)?,
        y_offset: u32_at(b, at + 0x34)?.cast_signed(),
    })
}

/// The camera blocks of a background file (`MAP1` section 0;
/// `rcbgLoadBin` `0x00921d60`: chunks of a 16-byte header, the tag and at
/// `+4` the body's length in 16-byte units, then the body; `YNDT` first,
/// `YNED` last), one per `YNSC` chunk in order.
#[must_use]
pub fn scene_cameras(sec: &[u8]) -> Vec<SceneCamera> {
    let mut out = Vec::new();
    let mut at = 0;
    let mut scene = 0;
    while at + 16 <= sec.len() {
        let tag = &sec[at..at + 4];
        if !tag.starts_with(b"YN") || tag == b"YNED" {
            break;
        }
        let len = u32_at(sec, at + 4).map_or(0, |l| l as usize * 16);
        let body = at + 16;
        if tag == b"YNSC" {
            if let Some(c) = sec
                .get(body..(body + len).min(sec.len()))
                .and_then(|b| scene_camera(scene, b))
            {
                out.push(c);
            }
            scene += 1;
        }
        at = body + len;
    }
    out
}

/// An entrance of the event script's table (`Event_GetEntranceAt`
/// `0x0086c020`; `Native_GetEntranceX/Y/Z/Yaw`, `Native_00c9_EntranceGetField6`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Entrance {
    pub index: usize,
    /// Its map index (`+0`; `AtelGetMapIndex`).
    pub map_index: u16,
    /// The camera entry the camera worker is signalled with (`+6`).
    pub camera: i16,
    /// Radians (`+8`).
    pub yaw: f32,
    /// Field units (`+0xc`).
    pub position: [f32; 3],
}

/// A script block's entrances (`+4` to `+8`, `0x20` bytes each).
#[must_use]
pub fn entrances(block: &[u8]) -> Vec<Entrance> {
    let (Some(first), Some(end)) = (u32_at(block, 4), u32_at(block, 8)) else {
        return Vec::new();
    };
    let (first, end) = (first as usize, end as usize);
    if first == 0 || end <= first {
        return Vec::new();
    }
    (0..(end - first) / 0x20)
        .filter_map(|i| {
            let o = first + 0x20 * i;
            Some(Entrance {
                index: i,
                map_index: u16_at(block, o)?,
                camera: i16_at(block, o + 6)?,
                yaw: f32_at(block, o + 8)?,
                position: vec3_at(block, o + 0xc)?,
            })
        })
        .collect()
}

/// A native call with constant arguments in a worker's code.
#[derive(Debug, Clone, PartialEq)]
pub struct Call {
    pub native: u16,
    /// Which entry it is in.
    pub entry: usize,
    pub args: Vec<f64>,
    /// After a conditional jump in its entry (it may not run).
    pub conditional: bool,
}

/// The natives this reads, with how many arguments each pops (script.md,
/// "Natives"): the rest leave the stack unknown.
const NATIVE_ARGS: [(u16, usize); 30] = [
    (0x0001, 1), // SetModel: the model id
    (0x0002, 3), // AttachCamera
    (0x0003, 1), // WorkerSetType3
    (0x0004, 1), // WorkerSetType4: a map group
    (0x0011, 2), // MapChange: map, entrance
    (0x0012, 2), // SetPosXZ
    (0x0013, 3), // setPosition: x, y, z
    (0x0025, 1), // PosSetYaw
    (0x0034, 1), // EnableTrigger: kind
    (0x0035, 1), // DisableTrigger
    (0x0054, 6), // TriggerSetLine: x0, y0, z0, x1, y1, z1
    (0x0055, 1), // TriggerSetRadius
    (0x0056, 1), // TriggerSetActive
    (0x0057, 3), // TriggerSetPoint: x, y, z
    (0x0058, 2), // TriggerSetPointDefault: half x, half z
    (0x0059, 3), // TriggerSetPoint (box): half x, half z, half height
    (0x005a, 1), // TriggerSetPointYaw
    (0x0061, 1), // SetTalkRange
    (0x007a, 2), // LineExtend: back, front
    (0x007f, 1), // SetTriggerMode
    (0x0085, 1), // SetTouchRange
    (0x0095, 1), // SetYaw
    (0x0096, 1), // SetPitch
    (0x00ab, 2), // MapChange (AtelSetEventJump2 mode 1)
    (0x010b, 2), // MapJump
    (0x010c, 2), // MapChangeDefault
    (0x0126, 3), // SetPos: x, y, z
    (0x0183, 1), // StopTurnSetAngle38
    (0x0197, 1), // WorkerAttachDobj: a display object
    (0x019d, 0), // Worker_Detach
];

/// The map change natives (map, entrance), as [`crate::exits`].
pub const MAP_CHANGES: [u16; 4] = [0x0011, 0x00ab, 0x010b, 0x010c];

fn args_of(native: u16) -> Option<usize> {
    NATIVE_ARGS
        .iter()
        .find(|(n, _)| *n == native)
        .map(|&(_, a)| a)
}

/// The calls of [`NATIVE_ARGS`] in worker `index` whose arguments are all
/// constants, entry by entry: a small stack of the pushed constants
/// (`pushimm`, `pushc`, `pushfc`, `neg` and the four sums of two), others
/// unknown, read straight through each entry.
#[must_use]
pub fn constant_calls(script: &ffx_script::Script, index: usize) -> Vec<Call> {
    let Some(worker) = script.workers().get(index) else {
        return Vec::new();
    };
    let Ok(code) = script.decode(worker.range.clone()) else {
        return Vec::new();
    };
    let mut starts: Vec<u32> = worker.entries.clone();
    starts.sort_unstable();
    let entry_of = |offset: u32| {
        worker
            .entries
            .iter()
            .enumerate()
            .filter(|(_, e)| **e <= offset)
            .max_by_key(|(_, e)| **e)
            .map_or(0, |(i, _)| i)
    };
    let mut out = Vec::new();
    let mut stack: Vec<Option<f64>> = Vec::new();
    let mut conditional = false;
    let mut entry = usize::MAX;
    for ins in &code {
        let here = entry_of(ins.offset);
        if here != entry || starts.binary_search(&ins.offset).is_ok() {
            entry = here;
            stack.clear();
            conditional = false;
        }
        let op = &OPCODES[usize::from(ins.opcode)];
        let operand = ins.operand;
        match (op.name, op.operand) {
            ("pushimm", _) => stack.push(operand.map(|x| f64::from(x.cast_signed()))),
            ("pushc", _) => stack.push(
                operand
                    .and_then(|x| script.int_const(index, x))
                    .map(f64::from),
            ),
            ("pushfc", _) => stack.push(
                operand
                    .and_then(|x| script.float_const(index, x))
                    .map(f64::from),
            ),
            ("neg", _) => {
                if let Some(t) = stack.last_mut() {
                    *t = t.map(|x| -x);
                }
            }
            (name @ ("add" | "sub" | "mul" | "div"), _) => {
                let rhs = stack.pop().flatten();
                let lhs = stack.pop().flatten();
                stack.push(lhs.zip(rhs).map(|(l, r)| match name {
                    "add" => l + r,
                    "sub" => l - r,
                    "mul" => l * r,
                    _ => l / r,
                }));
            }
            (name, Operand::Func) => {
                let native = operand.unwrap_or(0);
                if let Some(n) = args_of(native) {
                    if stack.len() >= n {
                        let args: Vec<Option<f64>> = stack.split_off(stack.len() - n);
                        if let Some(args) = args.into_iter().collect::<Option<Vec<f64>>>() {
                            out.push(Call {
                                native,
                                entry,
                                args,
                                conditional,
                            });
                        }
                    } else {
                        stack.clear();
                    }
                } else {
                    stack.clear();
                }
                if name == "func" {
                    stack.push(None);
                }
            }
            ("jnz" | "jz" | "popa_jnz" | "popa_jz", _) => {
                if op.name.starts_with("popa") {
                    stack.pop();
                }
                conditional = true;
            }
            (name, _) if name.starts_with("push") => stack.push(None),
            ("popva" | "popva_1", _) => {
                stack.pop();
                stack.pop();
            }
            (name, _) if name.starts_with("pop") || name == "endp" || name == "endp_1" => {
                stack.pop();
            }
            ("nop" | "nop_1d" | "nop_1e", _) => {}
            // anything else: what is on the stack is not known any more
            _ => stack.clear(),
        }
    }
    out
}

/// A character id as its model's name (`kind << 12 | number`: `c001`,
/// `n012`); `None` past the seven kinds.
#[must_use]
pub fn model_name(id: i64) -> Option<String> {
    let id = u32::try_from(id).ok()?;
    let kind = ['c', 'm', 'n', 's', 'w', 'f', 'k'].get((id >> 12) as usize)?;
    Some(format!("{kind}{:03}", id & 0xfff))
}

#[allow(clippy::cast_possible_truncation)] // constants the script pushed
fn int(v: f64) -> i64 {
    v as i64
}

/// A worker as JSON: its type, the first constant position, model, yaw,
/// trigger line or box, binding and talk/touch ranges its entries set
/// (`init`: entry 0's, else any entry's), its triggers enabled, the maps
/// it changes to (`exits`: map, entrance, entry).
fn worker_json(script: &ffx_script::Script, w: usize) -> Value {
    let kind = script.workers()[w].kind;
    let calls = constant_calls(script, w);
    // entry 0 first: where the worker is set up
    let first = |native: u16| {
        calls
            .iter()
            .filter(|c| c.native == native)
            .min_by_key(|c| (c.entry != 0, c.conditional, c.entry))
    };
    let mut o = serde_json::Map::new();
    o.insert("index".into(), json!(w));
    o.insert("type".into(), json!(kind));
    o.insert("entries".into(), json!(script.workers()[w].entries.len()));
    let v3 = |c: &Call, at: usize| json!([c.args[at], c.args[at + 1], c.args[at + 2]]);
    if let Some(c) = first(0x0013).or_else(|| first(0x0126)) {
        o.insert("position".into(), v3(c, 0));
        o.insert("positionEntry".into(), json!(c.entry));
    } else if let Some(c) = first(0x0012) {
        o.insert("positionXZ".into(), json!([c.args[0], c.args[1]]));
    }
    if let Some(c) = first(0x0001) {
        o.insert("modelId".into(), json!(int(c.args[0])));
        if let Some(n) = model_name(int(c.args[0])) {
            o.insert("model".into(), json!(n));
        }
    }
    if let Some(c) = first(0x0095).or_else(|| first(0x0025)) {
        o.insert("yaw".into(), json!(c.args[0]));
    }
    if let Some(c) = first(0x0054) {
        o.insert("line".into(), json!([v3(c, 0), v3(c, 3)]));
    }
    if let Some(c) = first(0x007a) {
        o.insert("lineExtend".into(), json!([c.args[0], c.args[1]]));
    }
    if let Some(c) = first(0x0057) {
        o.insert("point".into(), v3(c, 0));
    }
    if let Some(c) = first(0x0059) {
        o.insert("box".into(), json!([c.args[0], c.args[1], c.args[2]]));
    } else if let Some(c) = first(0x0058) {
        o.insert("box".into(), json!([c.args[0], c.args[1], null]));
    }
    if let Some(c) = first(0x005a) {
        o.insert("boxYaw".into(), json!(c.args[0]));
    }
    if let Some(c) = first(0x0055) {
        o.insert("radius".into(), json!(c.args[0]));
    }
    if let Some(c) = first(0x0085) {
        o.insert("touchRange".into(), json!(c.args[0]));
    }
    if let Some(c) = first(0x0061) {
        o.insert("talkRange".into(), json!(c.args[0]));
    }
    if let Some(c) = first(0x0004) {
        o.insert("group".into(), json!(int(c.args[0])));
    }
    if let Some(c) = first(0x0197) {
        o.insert("dobj".into(), json!(int(c.args[0])));
    }
    if let Some(c) = first(0x007f) {
        o.insert("triggerMode".into(), json!(int(c.args[0])));
    }
    let mut enabled: Vec<i64> = calls
        .iter()
        .filter(|c| c.native == 0x0034)
        .map(|c| int(c.args[0]))
        .collect();
    enabled.sort_unstable();
    enabled.dedup();
    if !enabled.is_empty() {
        o.insert("triggers".into(), json!(enabled));
    }
    let mut exits: Vec<Value> = Vec::new();
    for c in calls.iter().filter(|c| MAP_CHANGES.contains(&c.native)) {
        let e = json!({"map": int(c.args[0]), "entrance": int(c.args[1]), "entry": c.entry});
        if !exits
            .iter()
            .any(|x| x["map"] == e["map"] && x["entrance"] == e["entrance"])
        {
            exits.push(e);
        }
    }
    if !exits.is_empty() {
        o.insert("exits".into(), Value::Array(exits));
    }
    Value::Object(o)
}

/// An event script's data as JSON: the camera worker, the entrances, the
/// workers ([`worker_json`]).
#[must_use]
pub fn script_json(script: &ffx_script::Script) -> Value {
    let block = script.bytes();
    let camera = u16_at(block, 0x18).filter(|&c| c != 0xffff);
    let entrances: Vec<Value> = entrances(block)
        .iter()
        .map(|e| {
            json!({
                "index": e.index,
                "mapIndex": e.map_index,
                "camera": e.camera,
                "yaw": e.yaw,
                "position": e.position,
            })
        })
        .collect();
    let workers: Vec<Value> = (0..script.workers().len())
        .map(|w| worker_json(script, w))
        .collect();
    json!({
        "cameraWorker": camera,
        "entrances": entrances,
        "workers": workers,
    })
}

/// A walk mesh as JSON: its page, version, scale, the vertices (field
/// units, flat `x, y, z`) and the triangles (flat: three vertices, three
/// neighbours, the word).
#[must_use]
pub fn walk_mesh_json(m: &WalkMesh) -> Value {
    let vertices: Vec<f32> = m.vertices.iter().flatten().copied().collect();
    let triangles: Vec<i64> = m
        .triangles
        .iter()
        .flat_map(|t| {
            [
                i64::from(t.vertices[0]),
                i64::from(t.vertices[1]),
                i64::from(t.vertices[2]),
                i64::from(t.neighbours[0]),
                i64::from(t.neighbours[1]),
                i64::from(t.neighbours[2]),
                i64::from(t.word),
            ]
        })
        .collect();
    json!({
        "page": m.page,
        "version": m.version,
        "scale": m.scale,
        "vertices": vertices,
        "triangles": triangles,
    })
}

/// A `MAP1` file's walk meshes (sections 2 to 8 as pages 0 to 6, section 9
/// as page 7) and its scenes' cameras (section 0).
#[must_use]
pub fn map_file_json(file: &[u8]) -> Value {
    let sections = map1_sections(file);
    let walk: Vec<Value> = (2..=9u8)
        .filter_map(|s| {
            let sec = sections.get(usize::from(s)).copied().flatten()?;
            walk_mesh(s - 2, sec).map(|m| walk_mesh_json(&m))
        })
        .collect();
    let cameras: Vec<Value> = sections
        .first()
        .copied()
        .flatten()
        .map(scene_cameras)
        .unwrap_or_default()
        .iter()
        .map(|c| {
            json!({
                "scene": c.scene,
                "layers": c.layers,
                "eye": c.eye,
                "target": c.target,
                "up": c.up,
                "fov": c.fov,
                "yOffset": c.y_offset,
            })
        })
        .collect();
    json!({
        "sections": sections.iter().map(|s| s.map_or(0, <[u8]>::len)).collect::<Vec<_>>(),
        "walkMeshes": walk,
        "cameras": cameras,
    })
}

impl crate::Importer {
    /// `level`'s game data as JSON (the catalogue's level, e.g.
    /// `/map/bsil/bsil00/` or `bsil00`): `level`, `key`, and per event of the
    /// level (`events`: its name, map number, script data
    /// ([`script_json`]) and its `MAP1` file's data ([`map_file_json`]);
    /// events sharing a file share its data by `mapFile`), the files under
    /// `mapFiles`. `None` for a level the catalogue does not have.
    #[must_use]
    pub fn map_game_data(&self, level: &str) -> Option<Value> {
        let table = self.map_table();
        let key = crate::ids::level_key(level.trim_matches('/'));
        let l = table
            .levels
            .iter()
            .find(|l| l.level == level || l.key == level || l.key == key)?;
        let mut files = serde_json::Map::new();
        let mut events = Vec::new();
        for e in &l.events {
            let script = self
                .read(&e.script)
                .ok()
                .and_then(|b| ffx_script::Container::parse(b).ok())
                .map(|c| script_json(&c.script));
            if let Some(f) = &e.map_file
                && !files.contains_key(f)
                && let Ok(b) = self.read(f)
            {
                files.insert(f.clone(), map_file_json(&b));
            }
            events.push(json!({
                "name": e.name,
                "map": e.map,
                "mapIndex": e.map_index,
                "mapFile": e.map_file,
                "script": script,
            }));
        }
        Some(json!({
            "level": l.level,
            "key": l.key,
            "events": events,
            "mapFiles": files,
        }))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn put16(b: &mut [u8], o: usize, v: u16) {
        b[o..o + 2].copy_from_slice(&v.to_le_bytes());
    }

    fn put32(b: &mut [u8], o: usize, v: u32) {
        b[o..o + 4].copy_from_slice(&v.to_le_bytes());
    }

    #[test]
    fn a_walk_mesh_page() {
        // 3 vertices at 0x20, a triangle at 0x40, the group table at 0x50
        let mut s = vec![0u8; 0x60];
        put16(&mut s, 4, 0x1203);
        put16(&mut s, 6, 1);
        put16(&mut s, 0xa, 3);
        put32(&mut s, 0xc, 20f32.to_bits()); // scale 2: id units are field x 2
        put32(&mut s, 0x18, 0x20);
        put32(&mut s, 0x1c, 0x50);
        for (i, v) in [[0i16, 0, 0], [20, -4, 0], [0, 0, 40]].iter().enumerate() {
            for (k, c) in v.iter().enumerate() {
                put16(&mut s, 0x20 + 8 * i + 2 * k, c.cast_unsigned());
            }
        }
        for (k, v) in [0u16, 1, 2, 0xffff, 0xffff, 0xffff].iter().enumerate() {
            put16(&mut s, 0x40 + 2 * k, *v);
        }
        put32(&mut s, 0x4c, 0x0000_000e);
        put16(&mut s, 0x58, 1);
        put32(&mut s, 0x5c, 0x40);
        let m = walk_mesh(0, &s).expect("a page");
        assert_eq!(
            m.vertices,
            [[0.0, 0.0, 0.0], [10.0, -2.0, 0.0], [0.0, 0.0, 20.0]]
        );
        assert_eq!(m.triangles.len(), 1);
        assert_eq!(m.triangles[0].neighbours, [-1, -1, -1]);
        assert_eq!(m.triangles[0].kind(), 14);
        // a vertex number past the vertices: not a page
        put16(&mut s, 0x40, 3);
        assert!(walk_mesh(0, &s).is_none());
    }

    #[test]
    fn map1_sections_by_offset() {
        let mut f = vec![0u8; 0x40];
        f[..4].copy_from_slice(b"MAP1");
        put32(&mut f, 0x10, 0x20);
        put32(&mut f, 0x18, 0x30);
        let s = map1_sections(&f);
        assert_eq!(s[0].map(<[u8]>::len), Some(0x10));
        assert_eq!(s[1], None);
        assert_eq!(s[2].map(<[u8]>::len), Some(0x10));
        assert_eq!(map1_sections(b"MAPX").len(), 0);
    }

    #[test]
    fn a_scene_camera_after_its_layers() {
        // one layer without frames, an image transfer of 2 entries of 8 bytes
        let mut b = vec![0u8; 0x10 + 0x90 + 0x10 + 0x10 + 0x20 + 0x10 + 0x40];
        put32(&mut b, 0, 1);
        let t = 0x10 + 0x90 + 0x10 + 0x10;
        put32(&mut b, t + 0x18, 2);
        put32(&mut b, t + 0x1c, 8);
        let cam = t + 0x20 + 0x10;
        put32(&mut b, cam, 1f32.to_bits());
        put32(&mut b, cam + 0x30, 43.5f32.to_bits());
        put32(&mut b, cam + 0x34, (-3i32).cast_unsigned());
        let c = scene_camera(0, &b).expect("a camera");
        assert_eq!(c.eye, [1.0, 0.0, 0.0]);
        assert!((c.fov - 43.5).abs() < 1e-6);
        assert_eq!(c.y_offset, -3);
    }

    #[test]
    fn model_names() {
        assert_eq!(model_name(0x0001).as_deref(), Some("c001"));
        assert_eq!(model_name(0x2012).as_deref(), Some("n018"));
        assert_eq!(model_name(0x7000), None);
    }
}
