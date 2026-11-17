//! The battle maps a field map's random encounters use, read from the
//! game's memory (ffx-rust's docs/formats/battle.md, "The encounter
//! table", and docs/formats/ps2-disc.md), for loading the next battle's
//! map ahead (docs/architecture.md, "Map transitions").
//!
//! - The encounter table (`btl.bin`, read at the start by `MsBtlListInit`):
//!   the fields at `*0x0112a9c8` (14 bytes each: `i16` map number, `i16`
//!   the block's offset, `i16` the first formation, the name), their count
//!   at `0x0112a9d0` (an `i16`: the word's upper half holds something else), the blocks at `*0x0112a9cc` (`u8` formations, `u8`
//!   groups, then each group: `u8` n, `u16` battle map, `u8` encounter
//!   rate (0: no random encounters), `u8` weight total, n pairs). A map's
//!   field is the first with its number (`MsBtlListFieldNum`), the number
//!   being `g_SaveData + 4` (the map a random encounter looks up,
//!   `TkGetNowEventJumpMapNo`), not the event map at `+0`.
//! - A group's battle map: its low ten bits a number, `0x400` a battle map
//!   (CD directory 0x1a) else a field map (0x13); the map is files `2 n`
//!   and `2 n + 1` of the directory (`yiBattleMapRead`/`yiFieldMapRead`),
//!   whose file numbers start at the directory's entry of `cdrom.fid`
//!   (`g_CdLib + 0x2c`, an `i16` per directory) and whose names are in
//!   `cdrom.fnd` (`g_CdLib + 0x30`: `u32` count, `count + 1` `u32` offsets
//!   from the table's start, then the `host0:/ffx/master/...` paths;
//!   `PcFileName_00a42c00`).

use std::sync::Arc;

use ffx_bridge::Mem as _;
use ffx_host::arena::{Arena, ArenaMem};
use ffx_host::exec::Shared;

const FIELDS: u32 = 0x0112_a9c8;
const BLOCKS: u32 = 0x0112_a9cc;
const FIELD_COUNT: u32 = 0x0112_a9d0;
const FIELD_SIZE: u32 = 14;
/// `g_SaveData + 4`: `now_eventjump_map_no`.
const JUMP_MAP: u32 = 0x0112_ca94;
/// `g_CdLib_FidTable`, `g_CdLib_NameTable`.
const FID_TABLE: u32 = 0x0231_0c6c;
const NAME_TABLE: u32 = 0x0231_0c70;
/// The CD directories of battle and field maps.
const BATTLE_MAPS: u32 = 0x1a;
const FIELD_MAPS: u32 = 0x13;

/// One group of a field's encounters.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Group {
    /// The group's battle map word (`0x400` a battle map, low ten bits
    /// its number).
    pub map: u16,
    /// Its encounter rate (0: no random encounters).
    pub rate: u8,
    /// The map's first file's name in `cdrom.fnd` (empty without one).
    pub file: String,
}

/// The map a random encounter looks up now (`g_SaveData + 4`) and its
/// field's groups; `None` before the table is read, or for a map without
/// a field.
#[must_use]
pub fn current(shared: &Shared) -> Option<(u16, Vec<Group>)> {
    let mem = Mem::new(shared.arena());
    let map: u16 = mem.get(JUMP_MAP)?;
    Some((map, groups(&mem, map)?))
}

/// What `MsBtlListFieldNum` and `MsBtlListGroup` find for map `map`.
fn groups(mem: &Mem, map: u16) -> Option<Vec<Group>> {
    let fields: u32 = mem.get(FIELDS)?;
    let blocks: u32 = mem.get(BLOCKS)?;
    let count = u32::from(mem.get::<u16>(FIELD_COUNT)?);
    if fields == 0 || blocks == 0 || count == 0 || count > 0x400 {
        return None;
    }
    let field = (0..count)
        .map(|i| fields + i * FIELD_SIZE)
        .find(|&f| mem.get::<i16>(f) == Some(map.cast_signed()))?;
    let block = blocks.wrapping_add(u32::from(mem.get::<i16>(field + 2)?.cast_unsigned()));
    let n_groups: u8 = mem.get(block + 1)?;
    let mut at = block + 2;
    let mut out = Vec::new();
    for _ in 0..n_groups {
        let n: u8 = mem.get(at)?;
        let lo: u8 = mem.get(at + 1)?;
        let hi: u8 = mem.get(at + 2)?;
        let word = u16::from_le_bytes([lo, hi]);
        let rate: u8 = mem.get(at + 3)?;
        out.push(Group {
            map: word,
            rate,
            file: map_file(mem, word).unwrap_or_default(),
        });
        at += 5 + 2 * u32::from(n);
    }
    Some(out)
}

/// The name of the first file of map word `word` (`cdrom.fnd`).
fn map_file(mem: &Mem, word: u16) -> Option<String> {
    let dir = if word & 0x400 != 0 { BATTLE_MAPS } else { FIELD_MAPS };
    let fid: u32 = mem.get(FID_TABLE)?;
    let first: i16 = mem.get(fid + 2 * dir)?;
    if first < 0 || fid == 0 {
        return None;
    }
    let number = u32::from(first.cast_unsigned()) + 2 * u32::from(word & 0x3ff);
    file_name(mem, number)
}

/// Entry `n` of `cdrom.fnd`.
fn file_name(mem: &Mem, n: u32) -> Option<String> {
    let table: u32 = mem.get(NAME_TABLE)?;
    if table == 0 {
        return None;
    }
    let count: u32 = mem.get(table)?;
    if n >= count {
        return None;
    }
    let from: u32 = mem.get(table + 4 + 4 * n)?;
    let to: u32 = mem.get(table + 8 + 4 * n)?;
    if to <= from || to - from > 0x200 {
        return None;
    }
    let bytes: Vec<u8> = (from..to)
        .map_while(|o| mem.get::<u8>(table + o).filter(|&b| b != 0))
        .collect();
    Some(String::from_utf8_lossy(&bytes).into_owned())
}

/// The level a map file's path names, as the scene names its levels
/// (`World.level`): `/btlmap/<area>/<map>/` or `/map/<area>/<map>/`
/// (`host0:/ffx/master/jppc/btlmap/cdsp/cdsp00_a/...` is
/// `/btlmap/cdsp/cdsp00_a/`).
#[must_use]
pub fn level_of(path: &str) -> Option<String> {
    let parts: Vec<&str> = path.split('/').collect();
    let at = parts.iter().position(|p| *p == "btlmap" || *p == "map")?;
    let (area, map) = (parts.get(at + 1)?, parts.get(at + 2)?);
    if area.is_empty() || map.is_empty() || at + 3 >= parts.len() {
        return None;
    }
    Some(format!("/{}/{area}/{map}/", parts[at]))
}

/// The game's memory, read where it is mapped.
struct Mem {
    arena: Arc<Arena>,
}

impl Mem {
    fn new(arena: &Arc<Arena>) -> Self {
        Self {
            arena: Arc::clone(arena),
        }
    }

    fn get<T: ffx_bridge::Value>(&self, at: u32) -> Option<T> {
        if !self.arena.is_mapped(at, std::mem::size_of::<T>()) {
            return None;
        }
        let mem = ArenaMem {
            arena: Arc::clone(&self.arena),
        };
        Some(mem.read(at))
    }
}

#[cfg(test)]
mod tests {
    use super::level_of;

    #[test]
    fn a_map_files_level_is_the_folder_under_its_area() {
        assert_eq!(
            level_of("host0:/ffx/master/jppc/btlmap/cdsp/cdsp00_a/bin/mapout.vpa").as_deref(),
            Some("/btlmap/cdsp/cdsp00_a/")
        );
        assert_eq!(
            level_of("host0:/ffx/master/jppc/map/bsil/bsil02/x").as_deref(),
            Some("/map/bsil/bsil02/")
        );
        assert_eq!(level_of("host0:/ffx/master/jppc/event/x"), None);
        assert_eq!(level_of("host0:/ffx/master/jppc/map/bsil"), None);
    }
}
