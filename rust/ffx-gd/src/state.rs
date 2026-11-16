//! The first, small state API (G0): a few values read from the game's
//! memory for the debug view. G1 replaces it with ffx-rust's typed,
//! versioned game-state API (docs/architecture.md, "The game-state API").
//!
//! The addresses are `FFX.exe`'s (ffx-rust's `symbols/ffx.sym` and
//! docs/formats/save.md); the game's memory is ffx-rust's arena.

use std::sync::Arc;

use ffx_bridge::Mem as _;
use ffx_host::arena::{Arena, ArenaMem};
use ffx_host::exec::Shared;

/// `g_SaveData` (`0x0112ca90`), the game state block: `+0` the map, `+2` the
/// previous map, `+0xc` the entrance, `+0xbec` the story progress
/// (`g_SaveScenario`).
const SAVE_DATA: u32 = 0x0112_ca90;
const MAP: u32 = SAVE_DATA;
const PREVIOUS_MAP: u32 = SAVE_DATA + 2;
const ENTRANCE: u32 = SAVE_DATA + 0xc;
const STORY: u32 = SAVE_DATA + 0xbec;
/// Event control work 0's frame count: the running event's clock (the
/// replays pin their screenshots to it).
const EVENT_FRAME: u32 = 0x0132_5d94;

/// What the debug view shows of the game.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Snapshot {
    /// Updates run (frame boundaries entered: 30 a second).
    pub updates: u32,
    /// Frames presented (to the null device).
    pub flips: u32,
    /// The game's clock, seconds since its start.
    pub game_time: f64,
    /// The current map.
    pub map: u16,
    /// The map before it.
    pub previous_map: u16,
    /// The entrance the map was entered by.
    pub entrance: u8,
    /// The story progress.
    pub story: u16,
    /// The running event's frame count.
    pub event_frame: u32,
}

/// A value of the game's memory (0 where it is not mapped yet).
fn read<T: ffx_bridge::Value + Default>(arena: &Arc<Arena>, at: u32) -> T {
    if !arena.is_mapped(at, std::mem::size_of::<T>()) {
        return T::default();
    }
    let mem = ArenaMem {
        arena: Arc::clone(arena),
    };
    mem.read(at)
}

/// `len` bytes of the game's memory at `at` (at most 64 KiB; 0 where not
/// mapped): a developer's look at structures the game-state API does not
/// carry yet. Read only.
#[must_use]
pub fn peek(shared: &Shared, at: u32, len: usize) -> Vec<u8> {
    let arena = shared.arena();
    (0..len.min(0x1_0000))
        .map(|i| read::<u8>(arena, at.wrapping_add(u32::try_from(i).unwrap_or(0))))
        .collect()
}

/// `g_Equipment` (`0x01130f2c`): the inventory's 200 gear records of 22
/// bytes (ffx-rust docs/formats/kernel.md, "Equipment").
const EQUIPMENT: u32 = 0x0113_0f2c;
/// The 8 records of the `0x7000` ids and of the `0xb000` ids (the battle's
/// spoils), as `MsGetSaveWeapon` (`0x007abbd0`) finds them.
const EQUIPMENT_7000: u32 = 0x0231_0f9e;
const EQUIPMENT_B000: u32 = 0x0231_0dde;
const EQUIPMENT_SIZE: u32 = 22;
/// `g_SaveMenu` (save `+0x3d0c`): bit 3 the config's hiragana.
const SAVE_MENU: u32 = 0x0113_079c;

/// What names gear `id` (a `MsGetSaveWeapon` id: `0x7000 + i`, `0xb000 +
/// i`, else an inventory slot; an index out of range is slot 0): its
/// record's `w_name` record (`+0`), its character (`+4`) and the config's
/// hiragana bit, as `MsGetSaveWeaponName` (`0x007abdf0`) reads them;
/// `None` for id `0xff`, a record not in use (`+2` 0: the battle results
/// do not draw it) or a game not started. The state API does not carry
/// the records (yet): read from the game's memory now.
#[must_use]
pub fn gear(shared: &Shared, id: u16) -> Option<(u16, u8, bool)> {
    if id == 0xff {
        return None;
    }
    let arena = shared.arena();
    let i = u32::from(id & 0x0fff);
    let (base, n) = match id >> 12 {
        7 => (EQUIPMENT_7000, 8),
        0xb => (EQUIPMENT_B000, 8),
        _ => (EQUIPMENT, 200),
    };
    let slot = base + EQUIPMENT_SIZE * if i < n { i } else { 0 };
    if !arena.is_mapped(slot, EQUIPMENT_SIZE as usize) || read::<u8>(arena, slot + 2) == 0 {
        return None;
    }
    let kana = (read::<u32>(arena, SAVE_MENU) >> 3) & 1 != 0;
    Some((read(arena, slot), read(arena, slot + 4), kana))
}

/// The values now.
#[must_use]
pub fn snapshot(shared: &Shared) -> Snapshot {
    let clock = &shared.clock;
    let arena = shared.arena();
    Snapshot {
        updates: clock.frame(),
        flips: clock.flips(),
        game_time: clock.now() - ffx_host::clock::VIRTUAL_START,
        map: read(arena, MAP),
        previous_map: read(arena, PREVIOUS_MAP),
        entrance: read(arena, ENTRANCE),
        story: read(arena, STORY),
        event_frame: read(arena, EVENT_FRAME),
    }
}
