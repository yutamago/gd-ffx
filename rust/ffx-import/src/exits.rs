//! A map's exits, for loading the next map ahead (docs/architecture.md,
//! "Map transitions"): the event of each map number (`eventid.bin`, as the
//! game's `initEventList` reads it) and the maps an event's script can jump
//! to with constant arguments (the map change natives `0x0011`
//! `Native_MapChange`, `0x00ab` `Native_00ab_MapChange` and `0x010c`
//! `Native_010c_MapChangeDefault`: map, entrance; ffx-rust's
//! docs/formats/script.md).

use ffx_script::opcode::{OPCODES, Operand};

/// The map change natives: map, entrance.
const MAP_CHANGES: [u16; 3] = [0x0011, 0x00ab, 0x010c];

/// The event names by map number from `eventid.bin`: a table of `(offset,
/// _)` pairs whose first offset, over 8, is the count; each name a
/// zero-terminated string at its offset (`initEventList`, `0x00907f50`).
#[must_use]
pub fn event_names(file: &[u8]) -> Vec<String> {
    let word = |at: usize| {
        file.get(at..at + 4)
            .map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]) as usize)
    };
    let Some(first) = word(0) else {
        return Vec::new();
    };
    (0..first / 8)
        .map(|i| {
            word(i * 8)
                .and_then(|o| file.get(o..))
                .map(|s| {
                    let end = s.iter().position(|&b| b == 0).unwrap_or(s.len());
                    String::from_utf8_lossy(&s[..end]).into_owned()
                })
                .unwrap_or_default()
        })
        .collect()
}

/// The maps (and entrances) `script` jumps to with constant arguments: each
/// call of a map change native whose two arguments are pushed just before
/// it as constants (`pushimm` or `pushc`), in code order, without repeats.
#[must_use]
pub fn exits(script: &ffx_script::Script) -> Vec<(u16, u8)> {
    let op = |name: &str| OPCODES.iter().position(|o| o.name == name);
    let (Some(pushimm), Some(pushc)) = (op("pushimm"), op("pushc")) else {
        return Vec::new();
    };
    let mut out = Vec::new();
    for (w, worker) in script.workers().iter().enumerate() {
        let Ok(code) = script.decode(worker.range.clone()) else {
            continue;
        };
        let constant = |i: &ffx_script::Instr| -> Option<i32> {
            let v = i.operand?;
            match usize::from(i.opcode) {
                o if o == pushimm => Some(i32::from(v.cast_signed())),
                o if o == pushc => script.int_const(w, v),
                _ => None,
            }
        };
        for k in 2..code.len() {
            let call = &code[k];
            if OPCODES[usize::from(call.opcode)].operand != Operand::Func
                || !call.operand.is_some_and(|f| MAP_CHANGES.contains(&f))
            {
                continue;
            }
            let (Some(map), Some(entrance)) = (constant(&code[k - 2]), constant(&code[k - 1]))
            else {
                continue;
            };
            let (Ok(map), Ok(entrance)) = (u16::try_from(map), u8::try_from(entrance)) else {
                continue;
            };
            if !out.contains(&(map, entrance)) {
                out.push((map, entrance));
            }
        }
    }
    out
}

/// The level a map's event most likely shows: an event name `bsil0200` is
/// map `bsil02` of area `bsil` (`/map/bsil/bsil02/`, the scene's level name).
/// `None` for a name too short to hold one.
#[must_use]
pub fn event_level(event: &str) -> Option<String> {
    let e = event.trim().to_ascii_lowercase();
    if e.len() < 6 || !e.is_char_boundary(6) || !e.chars().all(|c| c.is_ascii_alphanumeric()) {
        return None;
    }
    Some(format!("/map/{}/{}/", &e[..4], &e[..6]))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn event_names_from_the_table() {
        // two entries: offsets 16 and 25
        let mut f = Vec::new();
        for (o, x) in [(16u32, 0u32), (25, 0)] {
            f.extend(o.to_le_bytes());
            f.extend(x.to_le_bytes());
        }
        f.extend(b"bsil0000\0bsil0200\0");
        assert_eq!(event_names(&f), ["bsil0000", "bsil0200"]);
        assert_eq!(event_names(&[]), Vec::<String>::new());
    }

    #[test]
    fn levels_of_event_names() {
        assert_eq!(
            event_level("bsil0200").as_deref(),
            Some("/map/bsil/bsil02/")
        );
        assert_eq!(
            event_level("KAMI0100").as_deref(),
            Some("/map/kami/kami01/")
        );
        assert_eq!(event_level("abc"), None);
    }
}
