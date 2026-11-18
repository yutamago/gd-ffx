//! Who made each effect draw (R7: effects by id; docs/remaster-plan.md, R7):
//! a stable owner for the draws of `ffx_host::vfx`'s capture, so a remade
//! effect can stand in for the classic one and the classic one's draws can
//! be hidden.
//!
//! A draw's instance key (`(lo, hi)`; ffx-rust's effects-draw.md,
//! "Instance keys and makers") names its maker by address: for a ppp
//! program `(g_pppCurrentProgram, object)`, the object at `hi`. Addresses
//! are not stable across runs; what owns the object is:
//!
//! - **A field program** (a map's own particle effect: the save sphere's
//!   glow, the sea's foam; scene-maps.md, "Map effects: the field-program
//!   table"): entry `i` of the map's table (`g_FpData`, `0x00d2c258`, `u16`
//!   count at `+4`) runs as the ppp part `g_FpRun + 0x80 i` (`0x00d2c260`),
//!   whose objects are listed from the part's `+0x30`, each linked by its
//!   first word (`pppCreatePObject`, ffx-vfx's part.rs). The owner is the
//!   entry: stable, the table is the map file's.
//! - **A spell overlay** (`magic_<id>.dll`): the object, or its birth
//!   record (`+8`), lies inside the overlay's image (`g_MagicFile_Handle`
//!   `0x00c64cac` up to its `SizeOfImage`; effects-draw.md, "Which draws are
//!   the spell's"). The owner is the spell (`g_MagicFile_MagicId`,
//!   `0x00c64ca0`).
//!
//! Other draws (the battle's shared effects, the op system's records, fixed
//! keys) have no owner yet (0).
//!
//! The owners are resolved on the game's main thread at the boundary of the
//! update the frame drew (a snapshot observer), while the objects the draws
//! name still exist, and kept for the last few updates; Godot's main thread
//! asks for the update it shows ([`owners`]).

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex, OnceLock, Weak};

use ffx_bridge::Mem as _;
use ffx_host::arena::{Arena, ArenaMem};
use ffx_host::exec::Shared;

/// `g_FpData`: the current map's field-program table (0: none).
const FP_DATA: u32 = 0x00d2_c258;
/// `g_FpRun`: one 0x80-byte ppp part per entry.
const FP_RUN: u32 = 0x00d2_c260;
const FP_RUN_SIZE: u32 = 0x80;
/// `g_FpDisp`: one 0x4c-byte display record per entry (`+0` flags: 0x10
/// hidden, the low four bits the sticking to a character or object).
const FP_DISP: u32 = 0x00d3_0260;
const FP_DISP_SIZE: u32 = 0x4c;
/// A part's object list head.
const PART_OBJECTS: u32 = 0x30;
/// `g_MagicFile_MagicId`, `g_MagicFile_Handle`.
const MAGIC_ID: u32 = 0x00c6_4ca0;
const MAGIC_HANDLE: u32 = 0x00c6_4cac;
/// At most this many objects are walked per part (a broken list stops).
const MAX_OBJECTS: usize = 4096;
/// The most entries a table has (113 in the archive's largest).
const MAX_ENTRIES: u32 = 512;

/// An owner's kind, in the owner word's top byte.
pub const KIND_FIELD: u32 = 1;
pub const KIND_MAGIC: u32 = 2;

/// An owner word: kind in bits 24..32, the entry or spell below.
#[must_use]
pub const fn owner(kind: u32, n: u32) -> u32 {
    (kind << 24) | (n & 0x00ff_ffff)
}

/// The owners of one frame's draws.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Owners {
    /// The update the frame drew.
    pub update: u32,
    /// One owner word per draw, in the frame's order (0: none).
    pub draws: Vec<u32>,
    /// Each field-program owner's place in render space (the entry's
    /// position, `(-x, -y, z)`), and whether the entry sticks to a
    /// character or object (its draws then tell its place better).
    pub places: Places,
}

/// The game's memory, read where it is mapped.
struct Mem {
    mem: ArenaMem,
    arena: Arc<Arena>,
}

impl Mem {
    fn new(arena: &Arc<Arena>) -> Self {
        Self {
            mem: ArenaMem {
                arena: Arc::clone(arena),
            },
            arena: Arc::clone(arena),
        }
    }

    fn u32(&self, at: u32) -> Option<u32> {
        (at != 0 && self.arena.is_mapped(at, 4)).then(|| self.mem.read(at))
    }

    fn u16(&self, at: u32) -> Option<u16> {
        (at != 0 && self.arena.is_mapped(at, 2)).then(|| self.mem.read(at))
    }

    fn f32(&self, at: u32) -> Option<f32> {
        self.u32(at).map(f32::from_bits)
    }
}

/// Each field-program owner's place and stuck flag ([`Owners::places`]).
type Places = Vec<(u32, [f32; 3], bool)>;

/// The objects of each running field-program entry: object address ->
/// entry index; and each entry's place.
fn field_objects(m: &Mem) -> (HashMap<u32, u32>, Places) {
    let mut objects = HashMap::new();
    let mut places = Vec::new();
    let Some(data) = m.u32(FP_DATA).filter(|&d| d != 0) else {
        return (objects, places);
    };
    let count = u32::from(m.u16(data + 4).unwrap_or(0)).min(MAX_ENTRIES);
    for i in 0..count {
        let part = FP_RUN + FP_RUN_SIZE * i;
        let mut obj = m.u32(part + PART_OBJECTS).unwrap_or(0);
        let mut n = 0;
        let mut any = false;
        while obj != 0 && n < MAX_OBJECTS {
            if objects.insert(obj, i).is_some() {
                break; // a loop
            }
            any = true;
            obj = m.u32(obj).unwrap_or(0);
            n += 1;
        }
        if any {
            let entry = data + 0x20 + 0x50 * i;
            let p = [
                -m.f32(entry).unwrap_or(0.0),
                -m.f32(entry + 4).unwrap_or(0.0),
                m.f32(entry + 8).unwrap_or(0.0),
            ];
            let stuck = m.u32(FP_DISP + FP_DISP_SIZE * i).unwrap_or(0) & 0xf != 0;
            places.push((owner(KIND_FIELD, i), p, stuck));
        }
    }
    (objects, places)
}

/// The loaded spell overlay's image range and id, if one is loaded.
fn magic_range(m: &Mem) -> Option<(u32, u32, u32)> {
    let base = m.u32(MAGIC_HANDLE).filter(|&h| h != 0)?;
    let id = m.u32(MAGIC_ID)?;
    if id == u32::MAX {
        return None;
    }
    let pe = base.wrapping_add(m.u32(base + 0x3c)?);
    if m.u32(pe)? != 0x4550 {
        return None;
    }
    let size = m.u32(pe + 0x50)?;
    Some((base, base.wrapping_add(size), id))
}

/// The owners of `frame`'s draws, read from the game's memory now.
fn resolve(m: &Mem, frame: &ffx_host::vfx::Frame) -> Owners {
    let (objects, places) = field_objects(m);
    let magic = magic_range(m);
    let draws = frame
        .draws
        .iter()
        .map(|d| {
            #[allow(clippy::cast_possible_truncation)] // the high word
            let hi = (d.key >> 32) as u32;
            if hi == 0 {
                return 0;
            }
            if let Some(&i) = objects.get(&hi) {
                return owner(KIND_FIELD, i);
            }
            if let Some((lo, end, id)) = magic {
                let inside = |a: u32| a >= lo && a < end;
                if inside(hi) || m.u32(hi.wrapping_add(8)).is_some_and(inside) {
                    return owner(KIND_MAGIC, id);
                }
            }
            0
        })
        .collect();
    Owners {
        update: frame.update,
        draws,
        places,
    }
}

/// How many updates' owners are kept.
const KEPT: usize = 6;

fn kept() -> &'static Mutex<VecDeque<Arc<Owners>>> {
    static K: OnceLock<Mutex<VecDeque<Arc<Owners>>>> = OnceLock::new();
    K.get_or_init(|| Mutex::new(VecDeque::new()))
}

/// The owners of the frame that drew update `update`, if still kept.
#[must_use]
pub fn owners(update: u32) -> Option<Arc<Owners>> {
    kept()
        .lock()
        .ok()?
        .iter()
        .rev()
        .find(|o| o.update == update)
        .cloned()
}

/// Registers the observer that resolves each update's draws on the game
/// `shared` (the boot's; only while the effect capture is on).
pub fn observe(shared: &Arc<Shared>) {
    let weak: Weak<Shared> = Arc::downgrade(shared);
    shared.observers.add(Arc::new(move |snap| {
        let Some(shared) = weak.upgrade() else { return };
        if !shared.vfx.enabled() {
            return;
        }
        let Some(frame) = shared.vfx.frame(snap.update) else {
            return;
        };
        let m = Mem::new(shared.arena());
        let o = Arc::new(resolve(&m, &frame));
        if let Ok(mut k) = kept().lock() {
            if k.len() >= KEPT {
                k.pop_front();
            }
            k.push_back(o);
        }
    }));
}

/// [`Owners`] as one array of words: the draw count, the owner of each draw,
/// then per place its owner, x, y, z (`f32` bits) and the stuck flag.
#[must_use]
pub fn encode(o: &Owners) -> Vec<i32> {
    let mut v = Vec::with_capacity(1 + o.draws.len() + 5 * o.places.len());
    v.push(i32::try_from(o.draws.len()).unwrap_or(i32::MAX));
    v.extend(o.draws.iter().map(|&w| w.cast_signed()));
    for &(w, p, stuck) in &o.places {
        v.push(w.cast_signed());
        v.extend(p.iter().map(|f| f.to_bits().cast_signed()));
        v.push(i32::from(stuck));
    }
    v
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn owner_words_and_their_encoding() {
        assert_eq!(owner(KIND_FIELD, 12), 0x0100_000c);
        assert_eq!(owner(KIND_MAGIC, 0x4c), 0x0200_004c);
        let o = Owners {
            update: 5,
            draws: vec![0, owner(KIND_FIELD, 3)],
            places: vec![(owner(KIND_FIELD, 3), [1.0, -2.0, 3.5], true)],
        };
        let v = encode(&o);
        assert_eq!(v[0], 2);
        assert_eq!(v[2], 0x0100_0003);
        assert_eq!(v[3], 0x0100_0003);
        assert_eq!(f32::from_bits(v[5].cast_unsigned()), -2.0);
        assert_eq!(v[7], 1);
    }
}
