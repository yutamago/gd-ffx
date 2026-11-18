//! The game's characters for the remaster's character tools (gd-ffx's R6,
//! docs/remaster-editor.md "Characters"): every character model in the
//! archive with its kind and name, its PS2 attachment points, and its
//! motions as glTF animations (ffx-rust's `ffx-motion`, as `ffx model
//! export` writes them), for the editor's preview. Read from the player's
//! archive only; nothing here goes into git.

use std::fmt::Write as _;

use serde_json::{Value, json};

use crate::{Error, Importer, Result};

/// The `chr/` folders by a model name's first letter (ffx-rust's
/// docs/formats/motion.md, "Characters").
const KINDS: [(char, &str, &str); 7] = [
    ('c', "pc", "party"),
    ('m', "mon", "monster"),
    ('n', "npc", "person"),
    ('s', "sum", "aeon"),
    ('w', "wep", "weapon"),
    ('f', "obj", "object"),
    ('k', "skl", "skeleton"),
];

/// The party's `ply_save` records by model (records 0-6 the party, 7
/// Seymour, 8.. the aeons: ffx-rust's docs/formats/kernel.md).
const PARTY: [(&str, u16); 8] = [
    ("c001", 0),
    ("c002", 1),
    ("c003", 2),
    ("c004", 3),
    ("c005", 4),
    ("c006", 5),
    ("c007", 6),
    ("c008", 7),
];

/// A character model of the archive.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Character {
    /// The model name (`c001`), the game's `Actor.model`.
    pub name: String,
    /// Its kind (`party`, `monster`, `person`, `aeon`, `weapon`, `object`,
    /// `skeleton`).
    pub kind: &'static str,
    /// The archive path of its HD model.
    pub model: String,
    /// Whether it has motion groups of its own (`mot/resident0..3.mgrp`).
    pub motions: bool,
}

/// An element of a character's PS2 skeleton ([`Importer::character_skeleton`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Bone {
    pub parent: Option<u16>,
    pub translation: [f32; 3],
    pub rotation: [f32; 4],
    pub scale: [f32; 3],
}

/// An attachment point of a character's PS2 model (`Ch_ElemGetAttr`,
/// ffx-rust's `ffx_chara::attach`): the `.chr` file's 16-byte entries from
/// the offset at `+0x20`, count at `+0x24`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Point {
    /// The point id (`u16` bits 0..13): 9 the head (`Ch_Neck`), 13 the jaw
    /// (`Ch_Mouth`), 0x13 and 0x14 the shoulders.
    pub id: u16,
    /// The kind (bits 14..15): 1 with an offset, 2 element only, 0 none.
    pub kind: u8,
    /// The element (bone `SkeletonNode_<element>`).
    pub element: i16,
    /// The offset as stored (kind 1: scaled by the game at run time).
    pub offset: [f32; 3],
}

/// The model name's kind and `chr/` folder, `None` for another name.
fn kind_of(name: &str) -> Option<(&'static str, &'static str)> {
    let c = name.chars().next()?;
    KINDS
        .iter()
        .find(|(l, _, _)| *l == c)
        .map(|(_, dir, kind)| (*dir, *kind))
}

/// The PS2 `.chr` path of a model (`ffx_ps2/ffx/master/jppc/chr/pc/c001/mdl/c001.chr`).
#[must_use]
pub fn chr_path(name: &str) -> Option<String> {
    let (dir, _) = kind_of(name)?;
    Some(format!(
        "ffx_ps2/ffx/master/jppc/chr/{dir}/{name}/mdl/{name}.chr"
    ))
}

/// A model's own motion groups (`resident0..3.mgrp`).
fn resident(dir: &str, name: &str) -> Vec<String> {
    (0..4)
        .map(|r| format!("ffx_ps2/ffx/master/jppc/chr/{dir}/{name}/mot/resident{r}.mgrp"))
        .collect()
}

/// The attachment points in a `.chr` file (empty when it has none or they
/// do not read).
#[must_use]
pub fn points_in(chr: &[u8]) -> Vec<Point> {
    let u32_at = |o: usize| {
        chr.get(o..o + 4)
            .map(|s| u32::from_le_bytes(s.try_into().expect("4 bytes")) as usize)
    };
    let (Some(at), Some(n)) = (u32_at(0x20), u32_at(0x24)) else {
        return Vec::new();
    };
    if n > 256 {
        return Vec::new();
    }
    (0..n)
        .map_while(|k| {
            let e = chr.get(at + 16 * k..at + 16 * k + 16)?;
            let word = u16::from_le_bytes([e[0], e[1]]);
            let f = |o: usize| f32::from_le_bytes(e[o..o + 4].try_into().expect("4 bytes"));
            #[allow(clippy::cast_possible_truncation)] // two bits
            Some(Point {
                id: word & 0x3fff,
                kind: (word >> 14) as u8,
                element: i16::from_le_bytes([e[2], e[3]]),
                offset: [f(4), f(8), f(12)],
            })
        })
        .collect()
}

/// The clips of `character`'s sequences in a motion group as glTF
/// animations, named `<group>/<clip>` with the motions that start by
/// playing them (as ffx-rust's `ffx model export` names them).
fn clip_animations(
    group: &ffx_motion::MotionGroup,
    stem: &str,
    character: u16,
) -> Vec<ffx_phyre::Animation> {
    let mut out = Vec::new();
    for seq in group.sequences.iter().filter(|s| s.character == character) {
        for (k, entry) in seq.clips.iter().enumerate() {
            let clip = &entry.clip;
            let mut motions: Vec<String> = seq
                .motions
                .iter()
                .filter(|m| {
                    m.script.first() == Some(&1)
                        && m.script
                            .get(1..3)
                            .map(|b| usize::from(u16::from_le_bytes([b[0], b[1]])))
                            == Some(k)
                })
                .map(|m| format!("{:04x}", m.number))
                .collect();
            motions.dedup();
            let mut name = format!("{stem}/{k}");
            if !motions.is_empty() {
                write!(name, " ({})", motions.join(" ")).expect("string");
            }
            let tracks = (0..clip.bones())
                .map(|bone| {
                    let mut track = ffx_phyre::Track {
                        node: format!("SkeletonNode_{bone}"),
                        ..Default::default()
                    };
                    for f in 0..usize::from(clip.frames) {
                        let (t, r, s) = ffx_motion::trs(clip.bone_at(bone, f));
                        track.translation.push(t);
                        track.rotation.push(r);
                        track.scale.push(s);
                    }
                    track
                })
                .collect();
            out.push(ffx_phyre::Animation {
                name,
                fps: f32::from(clip.rate) / 256.0,
                tracks,
            });
        }
    }
    out
}

impl Importer {
    /// Every character model in the archive (`chr/<kind>/<name>/mdl/d3d11/<name>.dae.phyre`),
    /// by name.
    #[must_use]
    pub fn characters(&self) -> Vec<Character> {
        let mut out: Vec<Character> = self
            .list("/chr/")
            .into_iter()
            .filter(|p| p.ends_with(".dae.phyre") && p.contains("/mdl/d3d11/"))
            .filter_map(|p| {
                let name = p
                    .rsplit('/')
                    .next()?
                    .strip_suffix(".dae.phyre")?
                    .to_string();
                let (dir, kind) = kind_of(&name)?;
                // the model's own folder (c001's model in chr/pc/c001/)
                if !p.contains(&format!("/chr/{dir}/{name}/")) {
                    return None;
                }
                let motions = resident(dir, &name).iter().any(|g| self.has(g));
                Some(Character {
                    name,
                    kind,
                    model: p,
                    motions,
                })
            })
            .collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out.dedup_by(|a, b| a.name == b.name);
        out
    }

    /// The name the game gives model `name` in `language`, encoded: the
    /// party's from the kernel's `ply_save` records (c001..c008), a
    /// monster's (`m<n>`) from record `n` of the kernel's monster tables
    /// (`monster1`..`3`, the battle's names); `None` for the others (people,
    /// objects and weapons have no name of their own in the kernel).
    #[must_use]
    pub fn character_name(&self, language: &str, name: &str) -> Option<Vec<u8>> {
        if let Some(&(_, record)) = PARTY.iter().find(|(m, _)| *m == name) {
            let t = self.kernel_table(language, "ply_save")?;
            return t
                .string(record, 0)
                .map(<[u8]>::to_vec)
                .filter(|s| !s.is_empty());
        }
        let number: u16 = name.strip_prefix('m')?.parse().ok()?;
        for table in ["monster1", "monster2", "monster3"] {
            if let Some(t) = self.kernel_table(language, table)
                && let Some(s) = t.string(number, 0)
            {
                return Some(s.to_vec()).filter(|s| !s.is_empty());
            }
        }
        None
    }

    /// The attachment points of model `name`'s PS2 model ([`points_in`]);
    /// empty without its `.chr`.
    #[must_use]
    pub fn character_points(&self, name: &str) -> Vec<Point> {
        chr_path(name)
            .and_then(|p| self.read(&p).ok())
            .map(|b| points_in(&b))
            .unwrap_or_default()
    }

    /// Model `name`'s PS2 skeleton (its `.chr` bind pose, which the game's
    /// motions animate: ffx-rust's docs/formats/motion.md, "Skeleton"): per
    /// element its parent (`None` for a root), translation, rotation
    /// (quaternion `[x, y, z, w]`, `T · Rz · Ry · Rx · S`) and scale (the
    /// integer part the game keeps). It has every element, also those the
    /// HD model has no node for (unskinned ends: the hands' weapon points).
    /// `None` without the `.chr` or when it does not read.
    #[must_use]
    pub fn character_skeleton(&self, name: &str) -> Option<Vec<Bone>> {
        let chr = self.read(&chr_path(name)?).ok()?;
        let skel = ffx_motion::Skeleton::from_chr(&chr).ok()?;
        Some(
            skel.bones
                .iter()
                .enumerate()
                .map(|(i, b)| {
                    let deg = |v: i16| (f32::from(v) / 100.0).to_radians();
                    Bone {
                        parent: (usize::from(b.parent) != i).then_some(b.parent),
                        translation: b.translation.map(|v| f32::from(v) / 1000.0),
                        rotation: ffx_motion::euler_xyz(
                            deg(b.rotation[0]),
                            deg(b.rotation[1]),
                            deg(b.rotation[2]),
                        ),
                        scale: b.scale.map(|v| (f32::from(v) / 4096.0).trunc().max(1.0)),
                    }
                })
                .collect(),
        )
    }

    /// The motions of model `name` as glTF animations: its own motion
    /// groups, or (a monster without its own that shares another's
    /// skeleton) those of the first character its `.chr` names
    /// ([`ffx_motion::referenced_characters`]).
    ///
    /// # Errors
    /// When a group does not parse.
    pub fn character_animations(&self, name: &str) -> Result<Vec<ffx_phyre::Animation>> {
        let (dir, _) = kind_of(name).ok_or_else(|| Error::Missing(name.to_string()))?;
        let character =
            ffx_motion::character_id(name).ok_or_else(|| Error::Missing(name.to_string()))?;
        let load = |groups: &[String],
                    character: u16,
                    out: &mut Vec<ffx_phyre::Animation>|
         -> Result<()> {
            for path in groups {
                if !self.has(path) {
                    continue;
                }
                let bytes = self.read(path)?;
                // 16-byte files are empty groups
                if bytes.len() <= 16 {
                    continue;
                }
                let group = ffx_motion::MotionGroup::parse(&bytes)
                    .map_err(|e| Error::Model(format!("{path}: {e}")))?;
                let stem = path
                    .rsplit('/')
                    .next()
                    .unwrap_or(path)
                    .trim_end_matches(".mgrp");
                out.extend(clip_animations(&group, stem, character));
            }
            Ok(())
        };
        let mut out = Vec::new();
        load(&resident(dir, name), character, &mut out)?;
        if out.is_empty()
            && let Some(chr) = chr_path(name).and_then(|p| self.read(&p).ok())
        {
            for other in ffx_motion::referenced_characters(&chr, character) {
                let Some(dir) = ffx_motion::character_dir(other) else {
                    continue;
                };
                let (kind, other_name) = dir.split_once('/').expect("kind/name");
                load(&resident(kind, other_name), other, &mut out)?;
                if !out.is_empty() {
                    break;
                }
            }
        }
        Ok(out)
    }

    /// Model `name` with its motions as glTF animations
    /// ([`Importer::character_animations`]), imported under `cache`
    /// (`<cache>/chr-motions/<name>.glb`): the editor's preview plays them.
    /// The game's own import ([`Importer::import_character`]) has none.
    ///
    /// # Errors
    /// As [`Importer::import_character`], and a group that does not parse.
    pub fn import_character_motions(
        &self,
        name: &str,
        cache: &std::path::Path,
    ) -> Result<std::path::PathBuf> {
        let path =
            crate::character_model_path(name).ok_or_else(|| Error::Missing(name.to_string()))?;
        let out = cache.join("chr-motions").join(format!("{name}.glb"));
        if out.is_file() && crate::glb_format(&out) == Some(crate::FORMAT) {
            return Ok(out);
        }
        let animations = self.character_animations(name)?;
        let glb = self.model_glb_with(&path, &animations)?;
        if let Some(dir) = out.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let tmp = out.with_extension("glb.part");
        std::fs::write(&tmp, glb)?;
        std::fs::rename(&tmp, &out)?;
        Ok(out)
    }

    /// The characters as JSON for the editor's browser: `[{"name", "kind",
    /// "motions", "names": {lang: name}, "points": [{"id", "kind",
    /// "element", "offset"}]}]`, the names decoded with `decode` (the game's
    /// encoding) in each of `languages` where the game gives one.
    pub fn character_list(
        &self,
        languages: &[&str],
        decode: impl Fn(&str, &[u8]) -> Option<String>,
    ) -> Value {
        let list: Vec<Value> = self
            .characters()
            .into_iter()
            .map(|c| {
                let mut names = serde_json::Map::new();
                for lang in languages {
                    if let Some(n) = self
                        .character_name(lang, &c.name)
                        .and_then(|b| decode(lang, &b))
                        .filter(|s| !s.trim().is_empty())
                    {
                        names.insert((*lang).to_string(), json!(n));
                    }
                }
                let points: Vec<Value> = self
                    .character_points(&c.name)
                    .iter()
                    .map(|p| json!({"id": p.id, "kind": p.kind, "element": p.element, "offset": p.offset}))
                    .collect();
                json!({"name": c.name, "kind": c.kind, "motions": c.motions, "names": names, "points": points})
            })
            .collect();
        Value::Array(list)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_attachment_points() {
        let mut chr = vec![0u8; 0x60];
        chr[0x20..0x24].copy_from_slice(&0x30u32.to_le_bytes());
        chr[0x24..0x28].copy_from_slice(&2u32.to_le_bytes());
        // point 9 (kind 0) on element 61; point 1 (kind 1) on 61 with an offset
        chr[0x30..0x32].copy_from_slice(&9u16.to_le_bytes());
        chr[0x32..0x34].copy_from_slice(&61i16.to_le_bytes());
        chr[0x40..0x42].copy_from_slice(&(0x4000u16 | 1).to_le_bytes());
        chr[0x42..0x44].copy_from_slice(&61i16.to_le_bytes());
        chr[0x48..0x4c].copy_from_slice(&0.5f32.to_le_bytes());
        let p = points_in(&chr);
        assert_eq!(p.len(), 2);
        assert_eq!((p[0].id, p[0].kind, p[0].element), (9, 0, 61));
        assert_eq!((p[1].id, p[1].kind, p[1].element), (1, 1, 61));
        assert!((p[1].offset[1] - 0.5).abs() < 1e-6);
        // a count past the file reads what is there
        chr[0x24..0x28].copy_from_slice(&5u32.to_le_bytes());
        assert_eq!(points_in(&chr).len(), 3);
        assert_eq!(
            chr_path("c001").as_deref(),
            Some("ffx_ps2/ffx/master/jppc/chr/pc/c001/mdl/c001.chr")
        );
        assert_eq!(chr_path("x001"), None);
    }

    /// On the player's game (skips without `FFX_GAME_DIR`): Tidus's points
    /// (the head 9 and the jaw 13 on elements 61 and 63), his motions, the
    /// character list.
    #[test]
    fn characters_from_the_players_archive() {
        let Some(dir) = std::env::var_os("FFX_GAME_DIR") else {
            eprintln!("FFX_GAME_DIR is not set; skipping");
            return;
        };
        let imp = Importer::open(std::path::Path::new(&dir)).unwrap();
        let points = imp.character_points("c001");
        let el = |id| points.iter().find(|p| p.id == id).map(|p| p.element);
        assert_eq!(el(9), Some(61), "{points:?}");
        assert_eq!(el(13), Some(63), "{points:?}");
        let chars = imp.characters();
        assert!(chars.len() > 500, "{} characters", chars.len());
        let c001 = chars.iter().find(|c| c.name == "c001").unwrap();
        assert_eq!(c001.kind, "party");
        assert!(c001.motions);
        // the PS2 skeleton: 136 elements, the hand's weapon point 24 under the hand 17
        let skel = imp.character_skeleton("c001").unwrap();
        assert_eq!(skel.len(), 136);
        assert_eq!(skel[0].parent, None);
        assert_eq!(skel[24].parent, Some(23));
        assert_eq!(skel[23].parent, Some(17));
        let anims = imp.character_animations("c001").unwrap();
        assert!(anims.len() > 10, "{} clips", anims.len());
        assert!(anims.iter().all(|a| a.fps > 0.0 && !a.tracks.is_empty()));
        let cs = imp.charset("us").unwrap();
        let name = |m| imp.character_name("us", m).map(|b| cs.decode(&b));
        assert_eq!(name("c001").as_deref(), Some("Tidus"));
        assert_eq!(name("c002").as_deref(), Some("Yuna"));
        assert_eq!(name("n001"), None);
    }
}
