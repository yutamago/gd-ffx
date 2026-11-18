//! The game's sounds by the ids it plays them by (R7: the editor's Audio
//! browser; ffx-rust's docs/formats/sound.md): names where the game gives
//! them and each sound's samples, decoded, to compare a replacement with.
//!
//! - **Music**: a music id below 0xb6 maps to an event of
//!   `sound_pc/music/ffx_music.fev`'s group `music` through `FFX.exe`'s
//!   table at `0x00b4f1c8` (`i32` per id, -1 unmapped); the event is named
//!   (`1_09_battle_theme`) and plays one sample of `ffx_music_bank00.fsb`.
//! - **Sound effects**: an effect id is a sequence number; its sample has
//!   that name (`50005`, or `50005_1` for a looping sound's further layers)
//!   in one of the banks `sound_pc/sfx/<language>/NNNN_bank00.fsb`.
//! - **Voice**: `sound_pc/voice/voiceidmapper.txt` (an `ML` file: `u32`
//!   count, then `u32` key and `u32` offset pairs, zero-terminated names)
//!   maps a voice id to its event's name (`210101td`), the name of its
//!   sample in one of `sound_pc/voice/<language>/*_bank00.fsb`.
//!
//! Every bank is FSB5 with IMA ADPCM ([`ffx_sound::fsb`], [`ffx_sound::ima`]);
//! only a bank's head and the one sample are read (the music bank is
//! 554 MB).

use std::collections::HashMap;
use std::path::Path;

use crate::{Error, Importer, Result};

const SOUND: &str = "ffx_data/gamedata/ps3data/sound_pc";
/// `FFX.exe`'s music id -> event index table (`0x00708be0` reads it).
const MUSIC_TABLE: u32 = 0x00b4_f1c8;
const MUSIC_IDS: u32 = 0xb6;

/// A sound the game plays by id.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Sound {
    pub id: u32,
    /// The name the game's data gives it (the music track, the sample, the
    /// voice event).
    pub name: String,
    /// The bank (archive path) and sample index it plays.
    pub bank: String,
    pub sample: u32,
}

/// A sample decoded: 16-bit interleaved frames.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Pcm {
    pub rate: u32,
    pub channels: u16,
    pub samples: Vec<i16>,
    /// Its loop points (frames), when it has them.
    pub looped: Option<(u32, u32)>,
}

/// Reads `exe`'s bytes at virtual address `va` (`n` of them) through its
/// section table.
fn exe_bytes(exe: &[u8], va: u32, n: usize) -> Option<&[u8]> {
    let u32_at = |at: usize| exe.get(at..at + 4).map(|s| u32::from_le_bytes(s.try_into().expect("4")));
    let u16_at = |at: usize| exe.get(at..at + 2).map(|s| u16::from_le_bytes(s.try_into().expect("2")));
    let pe = u32_at(0x3c)? as usize;
    let sections = usize::from(u16_at(pe + 6)?);
    let optional = usize::from(u16_at(pe + 20)?);
    let base = u32_at(pe + 24 + 28)?;
    let rva = va.checked_sub(base)?;
    for i in 0..sections {
        let s = pe + 24 + optional + 40 * i;
        let (vsize, vaddr, raw) = (u32_at(s + 8)?, u32_at(s + 12)?, u32_at(s + 20)?);
        if rva >= vaddr && rva < vaddr + vsize {
            let at = (raw + rva - vaddr) as usize;
            return exe.get(at..at + n);
        }
    }
    None
}

impl Importer {
    /// `len` bytes of archive file `path` from `start` (only the blocks
    /// holding them read).
    ///
    /// # Errors
    /// When the file is missing or unreadable.
    pub fn read_range(&self, path: &str, start: u64, len: usize) -> Result<Vec<u8>> {
        let entry = self
            .archive
            .find(path)
            .ok_or_else(|| Error::Missing(path.to_string()))?;
        let bad = |e: String| Error::Archive(format!("{path}: {e}"));
        let blocks = self.archive.blocks(entry).map_err(|e| bad(e.to_string()))?;
        let end = start.saturating_add(len as u64).min(entry.size);
        let mut out = Vec::with_capacity(len);
        let mut file = std::fs::File::open(&self.vbf).map_err(|e| bad(e.to_string()))?;
        let mut at = 0u64;
        for b in blocks {
            let (from, to) = (at, at + b.size as u64);
            at = to;
            if to <= start || from >= end {
                continue;
            }
            let mut stored = vec![0u8; b.stored];
            {
                use std::io::{Read as _, Seek as _, SeekFrom};
                file.seek(SeekFrom::Start(b.offset)).map_err(|e| bad(e.to_string()))?;
                file.read_exact(&mut stored).map_err(|e| bad(e.to_string()))?;
            }
            let data = if b.is_compressed() {
                let mut d = Vec::with_capacity(b.size);
                std::io::copy(&mut flate2::read::ZlibDecoder::new(&stored[..]), &mut d).map_err(|e| bad(e.to_string()))?;
                d
            } else {
                stored
            };
            let lo = usize::try_from(start.saturating_sub(from)).unwrap_or(0);
            let hi = usize::try_from(end.min(to) - from).unwrap_or(0).min(data.len());
            if lo < hi {
                out.extend_from_slice(&data[lo..hi]);
            }
        }
        Ok(out)
    }

    /// A bank's head parsed (its samples' names, formats and data ranges).
    fn bank_head(&self, path: &str) -> Option<ffx_sound::fsb::Bank> {
        let first = self.read_range(path, 0, 0x3c).ok()?;
        let n = ffx_sound::fsb::Bank::head_len(&first)?;
        let head = self.read_range(path, 0, n).ok()?;
        ffx_sound::fsb::Bank::parse_head(&head).ok()
    }

    /// The music ids the game maps to tracks, with the tracks' names
    /// (`exe`: the player's `FFX.exe`, read for the id table).
    #[must_use]
    pub fn music_sounds(&self, exe: &Path) -> Vec<Sound> {
        let Ok(exe) = std::fs::read(exe) else {
            return Vec::new();
        };
        let Some(table) = exe_bytes(&exe, MUSIC_TABLE, 4 * MUSIC_IDS as usize) else {
            return Vec::new();
        };
        let Ok(fev) = self.read(&format!("{SOUND}/music/ffx_music.fev")) else {
            return Vec::new();
        };
        let Ok(project) = ffx_sound::fev::Project::parse(&fev) else {
            return Vec::new();
        };
        let Some(group) = project.group("music") else {
            return Vec::new();
        };
        let bank = format!("{SOUND}/music/ffx_music_bank00.fsb");
        let mut out = Vec::new();
        for id in 0..MUSIC_IDS {
            let at = 4 * id as usize;
            let index = i32::from_le_bytes(table[at..at + 4].try_into().expect("4"));
            let Some(event) = usize::try_from(index).ok().and_then(|i| group.events.get(i)) else {
                continue;
            };
            let Some((wave, _)) = project.wave(event) else {
                continue;
            };
            out.push(Sound {
                id,
                name: event.name.clone(),
                bank: bank.clone(),
                sample: wave.index,
            });
        }
        out
    }

    /// Every sound effect id of `language` (`us`, `jp`) with its sample (the
    /// banks' heads are read: about 600 banks).
    #[must_use]
    pub fn sfx_sounds(&self, language: &str) -> Vec<Sound> {
        let dir = format!("{SOUND}/sfx/{language}/");
        let mut out: HashMap<u32, Sound> = HashMap::new();
        for bank in self.list(&dir) {
            if !bank.ends_with("_bank00.fsb") {
                continue;
            }
            let Some(head) = self.bank_head(&bank) else { continue };
            for (i, s) in head.samples.iter().enumerate() {
                let number = s.name.split('_').next().unwrap_or("");
                let Ok(id) = number.parse::<u32>() else { continue };
                // the sound's first sample (a looping sound's layers are `_1`, `_2`, ...)
                if out.get(&id).is_some_and(|x| x.name == number) {
                    continue;
                }
                out.insert(
                    id,
                    Sound {
                        id,
                        name: s.name.clone(),
                        bank: bank.clone(),
                        sample: u32::try_from(i).unwrap_or(0),
                    },
                );
            }
        }
        let mut v: Vec<Sound> = out.into_values().collect();
        v.sort_by_key(|s| s.id);
        v
    }

    /// Every voice id with its event's name and sample in `language`'s banks.
    #[must_use]
    pub fn voice_sounds(&self, language: &str) -> Vec<Sound> {
        let Ok(ml) = self.read(&format!("{SOUND}/voice/voiceidmapper.txt")) else {
            return Vec::new();
        };
        let names = ml_names(&ml);
        let dir = format!("{SOUND}/voice/{language}/");
        let mut samples: HashMap<String, (String, u32)> = HashMap::new();
        for bank in self.list(&dir) {
            if !bank.ends_with("_bank00.fsb") {
                continue;
            }
            if let Some(head) = self.bank_head(&bank) {
                for (i, s) in head.samples.iter().enumerate() {
                    samples.insert(s.name.to_ascii_lowercase(), (bank.clone(), u32::try_from(i).unwrap_or(0)));
                }
            }
        }
        let mut v: Vec<Sound> = names
            .into_iter()
            .filter_map(|(id, name)| {
                let (bank, sample) = samples.get(&name.to_ascii_lowercase())?.clone();
                Some(Sound { id, name, bank, sample })
            })
            .collect();
        v.sort_by_key(|s| s.id);
        v
    }

    /// Sample `sample` of bank `bank` (an archive path), decoded.
    #[must_use]
    pub fn sound_pcm(&self, bank: &str, sample: u32) -> Option<Pcm> {
        let head = self.bank_head(bank)?;
        let s = head.samples.get(sample as usize)?;
        let data = self
            .read_range(bank, s.data.start as u64, s.data.end - s.data.start)
            .ok()?;
        Some(Pcm {
            rate: s.rate,
            channels: s.channels,
            samples: ffx_sound::ima::decode(&data, s.channels, s.frames),
            looped: s.looped,
        })
    }
}

/// An `ML` file's (key, name) pairs.
fn ml_names(b: &[u8]) -> Vec<(u32, String)> {
    let u32_at = |at: usize| b.get(at..at + 4).map(|s| u32::from_le_bytes(s.try_into().expect("4")));
    if b.get(..2) != Some(b"ML") {
        return Vec::new();
    }
    let n = u32_at(4).unwrap_or(0).min(1 << 20) as usize;
    let mut out = Vec::with_capacity(n);
    for i in 0..n {
        let (Some(key), Some(off)) = (u32_at(8 + 8 * i), u32_at(12 + 8 * i)) else { break };
        let at = off as usize;
        let Some(rest) = b.get(at..) else { continue };
        let end = rest.iter().position(|&c| c == 0).unwrap_or(rest.len());
        out.push((key, String::from_utf8_lossy(&rest[..end]).into_owned()));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ml_files_map_keys_to_names() {
        let mut b = b"ML\0\0".to_vec();
        b.extend(2u32.to_le_bytes());
        b.extend(7u32.to_le_bytes());
        b.extend(24u32.to_le_bytes());
        b.extend(9u32.to_le_bytes());
        b.extend(28u32.to_le_bytes());
        b.extend(b"abc\0de\0");
        assert_eq!(ml_names(&b), vec![(7, "abc".into()), (9, "de".into())]);
    }
}
