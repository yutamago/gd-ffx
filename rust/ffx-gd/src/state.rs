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
