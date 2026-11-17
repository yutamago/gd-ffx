//! The map catalogue (docs/importer.md, "The map catalogue"): every field
//! map and battle stage of the player's archive with its level, events, map
//! numbers, place names in the game's languages and area, read from the
//! game's own files the way its loaders read them (ffx-remix's
//! docs/offline-scene.md, "Map catalogue", by the same author, describes the
//! chain; this is a port of the method, not of its files):
//!
//! - `eventid.bin` (`initEventList` `0x00907f50`): the event names by map
//!   number ([`crate::exits::event_names`]).
//! - `cdrom.fnd`/`cdrom.fid` (`PcFileName_00a42c00`): the event module
//!   (`0xc`) holds `0x12` slots per event, slot 0 its script (`.ebp`); the
//!   map module (`0x13`) two per map index: `2 i` the PS2 background
//!   (`mapout.txa`), `2 i + 1` the `MAP1` file (`mapout.vpa`,
//!   `yiMapReadMaster` `0x009093b0`).
//! - The script's header: the entrance table (`+4..+8`, `0x20` bytes an
//!   entrance), entrance 0's map index (`AtelGetMapIndex` `0x0086bd90`) and
//!   the place-name index (`+0x1e`; bit 15: entry 0 of the table at `+0x28`;
//!   `AtelSetSaveDicIndex` `0x00870ef0`).
//! - The level: the map file's folder between `jppc/` and `/bin/`
//!   (`graphicLevelAnalysisName` `0x00641d00`), e.g. `map/bsil/bsil00`; the
//!   PC models under `ffx_data/gamedata/ps3data/<level>/`.
//! - Place names: section 11 of each language's `macrodic.dcp`
//!   ([`crate::place_name_in`]), decoded with its character set.
//! - Battle stages: every `ps3data/btlmap/<area>/<stage>/mdl/d3d11/<stage>.dae.phyre`.
//!
//! gd-ffx keys a map by its level (`bsil00`, the game's `World.level`
//! `/map/bsil/bsil00/`); several events can show one level, so a
//! [`Level`] lists its [`Event`]s.

use std::collections::BTreeMap;

use serde_json::{Value, json};

use crate::{Importer, ids};

const CDDATA: &str = "ffx_ps2/ffx/proj/battle/jp/cddata/";
const EVENT_MODULE: usize = 0xc;
const MAP_MODULE: usize = 0x13;
const FILES_PER_EVENT: usize = 0x12;
const PS3DATA: &str = "ffx_data/gamedata/ps3data/";

/// Text before these in a place name is its area ("Besaid - Beach").
const PLACE_SEPARATORS: [&str; 5] = [" - ", "・", " – ", ": ", "："];

/// One event (a field map number) of a level.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Event {
    /// The event name (`bsil0000`).
    pub name: String,
    /// Its map number (`World.map`, `AtelSetEventJump2`'s map).
    pub map: u16,
    /// Entrance 0's map index (the map module's slot pair).
    pub map_index: u16,
    /// Entrances in the script's table.
    pub entrances: u16,
    /// The place-name index (the save screen's dictionary); `None` for
    /// place 0, the placeholder of test and system maps.
    pub place_index: Option<u16>,
    /// The event script's archive path.
    pub script: String,
}

/// One level: a field map's level folder (with its events) or a battle stage.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Level {
    /// `field` or `battle`.
    pub kind: &'static str,
    /// The game's level name (`/map/bsil/bsil00/`, `/btlmap/bika/bika00_a/`).
    pub level: String,
    /// gd-ffx's key ([`ids::level_key`]: `bsil00`).
    pub key: String,
    /// The area code: the level's parent folder (`bsil`).
    pub code: String,
    /// The 3D model's archive path, when there is one.
    pub model_3d: Option<String>,
    /// The 2D layers' archive path, when there are some.
    pub model_2d: Option<String>,
    /// The events showing it, by map number (none for a battle stage).
    pub events: Vec<Event>,
}

/// An event without a map, and why: `no_script` (no slot or no file),
/// `not_ev` (not an event script), `no_map` (no entrance table or map
/// index), `no_level` (its map file names no level), `no_files` (no PC
/// models in its level).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Skipped {
    pub map: u16,
    pub name: String,
    pub reason: &'static str,
}

/// The language-independent catalogue.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Table {
    /// Field levels in the order of their first event, then battle stages by name.
    pub levels: Vec<Level>,
    pub skipped: Vec<Skipped>,
    /// Events in `eventid.bin` (named or not).
    pub events: usize,
}

fn u32_at(b: &[u8], o: usize) -> Option<u32> {
    b.get(o..o + 4)
        .map(|s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
}

fn u16_at(b: &[u8], o: usize) -> Option<u16> {
    b.get(o..o + 2).map(|s| u16::from_le_bytes([s[0], s[1]]))
}

/// `cdrom.fnd` names by location, with `cdrom.fid`'s first location of
/// each module (`PcFileName_00a42c00`).
struct Names {
    fnd: Vec<u8>,
    count: usize,
    firsts: Vec<i16>,
}

impl Names {
    fn read(imp: &Importer) -> Option<Self> {
        let fnd = imp.read(&format!("{CDDATA}cdrom.fnd")).ok()?;
        let fid = imp.read(&format!("{CDDATA}cdrom.fid")).ok()?;
        let count = u32_at(&fnd, 0)? as usize;
        let firsts = fid
            .as_chunks::<2>()
            .0
            .iter()
            .map(|c| i16::from_le_bytes(*c))
            .collect();
        Some(Self { fnd, count, firsts })
    }

    fn name(&self, loc: usize) -> Option<String> {
        if loc >= self.count {
            return None;
        }
        let s = u32_at(&self.fnd, 4 + 4 * loc)? as usize;
        let e = u32_at(&self.fnd, 8 + 4 * loc)? as usize;
        let b = self.fnd.get(s..e).filter(|b| !b.is_empty())?;
        let b = &b[..b.iter().position(|&c| c == 0).unwrap_or(b.len())];
        Some(b.iter().map(|&c| char::from(c)).collect())
    }

    /// Module `m`'s first location and the end of its slots.
    fn span(&self, m: usize) -> Option<(usize, usize)> {
        let first = *self.firsts.get(m)?;
        let next = self
            .firsts
            .iter()
            .copied()
            .filter(|&f| f > first)
            .min()
            .map_or(self.count, |f| usize::try_from(f).unwrap_or(self.count));
        Some((usize::try_from(first).ok()?, next))
    }
}

/// `host0:/ffx/...` as the archive's path (`ffx_ps2/ffx/...`), lower case.
fn archive_path(host: &str) -> String {
    host.strip_prefix("host0:")
        .map_or_else(|| host.to_owned(), |rest| format!("ffx_ps2{rest}"))
        .to_ascii_lowercase()
}

/// The level folder of a map module name: the path between `jppc/` and
/// `/bin/` (`host0:/ffx/master/jppc/map/kami/kami01/bin/mapout.txa` →
/// `map/kami/kami01`).
fn level_folder(name: &str) -> Option<String> {
    let lower = name.to_ascii_lowercase();
    let start = lower.find("/jppc/")? + "/jppc/".len();
    let end = lower[start..].find("/bin/")? + start;
    Some(lower[start..end].to_owned())
}

/// An event script's entrances, entrance 0's map index and place index
/// (`None`: not an event script).
fn script_header(ebp: &[u8]) -> Option<(u16, Option<u16>, u16)> {
    if !ebp.starts_with(b"EV") {
        return None;
    }
    let hdr = u32_at(ebp, 4)? as usize;
    let first = u32_at(ebp, hdr + 4)? as usize;
    let end = u32_at(ebp, hdr + 8)? as usize;
    let (entrances, map) = if first == 0 {
        (0, None)
    } else {
        let n = u16::try_from(end.saturating_sub(first) / 0x20).unwrap_or(u16::MAX);
        (n, u16_at(ebp, hdr + first))
    };
    let place = u16_at(ebp, hdr + 0x1e)?;
    let place = if place & 0x8000 == 0 {
        place
    } else {
        let t = u32_at(ebp, hdr + 0x28)? as usize;
        u16_at(ebp, hdr + t)?
    };
    Some((entrances, map, place))
}

impl Importer {
    /// The archive's spelling of `path` (lower case) if it has the file.
    fn existing(&self, path: &str) -> Option<String> {
        self.has(path).then(|| path.to_ascii_lowercase())
    }

    /// The level's PC models: (3D, 2D).
    fn level_models(&self, folder: &str) -> (Option<String>, Option<String>) {
        let leaf = folder.rsplit('/').next().unwrap_or(folder);
        let base = format!("{PS3DATA}{folder}/");
        (
            self.existing(&format!("{base}mdl/d3d11/{leaf}.dae.phyre")),
            self.existing(&format!("{base}2d/mdl/d3d11/{leaf}.dae.phyre")),
        )
    }

    /// The language-independent map catalogue ([`Table`]), read once
    /// (every event script: about 400 files).
    #[must_use]
    pub fn map_table(&self) -> std::sync::Arc<Table> {
        self.maps
            .get_or_init(|| std::sync::Arc::new(self.read_map_table()))
            .clone()
    }

    fn read_map_table(&self) -> Table {
        let mut table = Table::default();
        let events = self
            .list("/event/header/eventid.bin")
            .first()
            .and_then(|p| self.read(p).ok())
            .map(|f| crate::exits::event_names(&f))
            .unwrap_or_default();
        table.events = events.len();
        let names = Names::read(self);
        let ev = names.as_ref().and_then(|n| n.span(EVENT_MODULE));
        let mp = names.as_ref().and_then(|n| n.span(MAP_MODULE));
        let mut by_folder: BTreeMap<String, usize> = BTreeMap::new();
        for (id, name) in events.iter().enumerate() {
            let name = name.to_ascii_lowercase();
            let Ok(map) = u16::try_from(id) else { break };
            if name.is_empty() {
                continue;
            }
            let skip = |reason| Skipped {
                map,
                name: name.clone(),
                reason,
            };
            let script = names
                .as_ref()
                .zip(ev)
                .and_then(|(n, (first, _))| n.name(first + id * FILES_PER_EVENT))
                .map(|n| archive_path(&n))
                .filter(|p| self.has(p));
            let Some(script) = script else {
                table.skipped.push(skip("no_script"));
                continue;
            };
            let Some((entrances, map_index, place)) =
                self.read(&script).ok().as_deref().and_then(script_header)
            else {
                table.skipped.push(skip("not_ev"));
                continue;
            };
            let slot = map_index
                .map(usize::from)
                .filter(|&i| i > 0)
                .filter(|i| mp.is_some_and(|(first, next)| first + 2 * i + 1 < next));
            let (Some(slot), Some(names), Some((first, _))) = (slot, names.as_ref(), mp) else {
                table.skipped.push(skip("no_map"));
                continue;
            };
            let folder = names
                .name(first + 2 * slot)
                .as_deref()
                .and_then(level_folder)
                .or_else(|| {
                    names
                        .name(first + 2 * slot + 1)
                        .as_deref()
                        .and_then(level_folder)
                });
            let Some(folder) = folder else {
                table.skipped.push(skip("no_level"));
                continue;
            };
            let event = Event {
                name: name.clone(),
                map,
                map_index: u16::try_from(slot).unwrap_or(u16::MAX),
                entrances,
                place_index: (place != 0).then_some(place),
                script,
            };
            if let Some(&i) = by_folder.get(&folder) {
                table.levels[i].events.push(event);
                continue;
            }
            let (model_3d, model_2d) = self.level_models(&folder);
            if model_3d.is_none() && model_2d.is_none() {
                table.skipped.push(skip("no_files"));
                continue;
            }
            let code = folder.split('/').nth(1).unwrap_or_default().to_owned();
            by_folder.insert(folder.clone(), table.levels.len());
            table.levels.push(Level {
                kind: "field",
                level: format!("/{folder}/"),
                key: ids::level_key(&folder),
                code,
                model_3d,
                model_2d,
                events: vec![event],
            });
        }
        table.levels.extend(self.battle_stages());
        table
    }

    /// The battle stages: every
    /// `ps3data/btlmap/<area>/<stage>/mdl/d3d11/<stage>.dae.phyre`, by name.
    fn battle_stages(&self) -> Vec<Level> {
        let mut out = Vec::new();
        let mut stages = BTreeMap::new();
        for p in self.list(&format!("{PS3DATA}btlmap/")) {
            let rest = &p[PS3DATA.len() + "btlmap/".len()..];
            let parts: Vec<&str> = rest.split('/').collect();
            if let [area, stage, "mdl", "d3d11", file] = parts.as_slice()
                && *file == format!("{stage}.dae.phyre")
            {
                stages.insert((*stage).to_owned(), ((*area).to_owned(), p.clone()));
            }
        }
        for (stage, (area, model)) in stages {
            out.push(Level {
                kind: "battle",
                level: format!("/btlmap/{area}/{stage}/"),
                key: stage.clone(),
                code: area,
                model_3d: Some(model),
                model_2d: None,
                events: Vec::new(),
            });
        }
        out
    }

    /// The place names of `language` by index (the save screen's
    /// dictionary, decoded); empty without its dictionary or character set.
    #[must_use]
    pub fn place_names(&self, language: &str) -> Vec<String> {
        let Some(lang) = ffx_text::Language::from_code(language) else {
            return Vec::new();
        };
        let (Some(dic), Ok(charset)) = (
            self.read(&format!(
                "ffx_ps2/ffx/master/new_{}pc/menu/macrodic.dcp",
                lang.code()
            ))
            .ok(),
            self.charset(language),
        ) else {
            return Vec::new();
        };
        let Some(sec) = u32_at(&dic, 11 * 4)
            .map(|s| s as usize)
            .filter(|&s| s > 0 && s < dic.len())
        else {
            return Vec::new();
        };
        let count = usize::from(u16_at(&dic, sec).unwrap_or(0) >> 2);
        (0..count)
            .map(|i| {
                let i = u16::try_from(i).unwrap_or(u16::MAX);
                let text = crate::place_name_in(&dic, i)
                    .map(|b| charset.decode(&b))
                    .unwrap_or_default();
                // Korean shows index 10 as a space (ffx-remix's maps.rs)
                let text = if lang == ffx_text::Language::Korean {
                    text.replace("{c:a}", " ")
                } else {
                    text
                };
                text.trim().to_owned()
            })
            .collect()
    }

    /// The map catalogue as JSON ([`catalogue_json`]) with place names in
    /// `languages` (codes of `ffx_text::Language::from_code`; empty: all
    /// eight).
    #[must_use]
    pub fn map_catalogue(&self, languages: &[&str]) -> Value {
        let langs: Vec<&'static str> = if languages.is_empty() {
            ffx_text::Language::ALL.iter().map(|l| l.code()).collect()
        } else {
            languages
                .iter()
                .filter_map(|l| ffx_text::Language::from_code(l))
                .map(ffx_text::Language::code)
                .collect()
        };
        let places: Vec<(&str, Vec<String>)> =
            langs.iter().map(|&l| (l, self.place_names(l))).collect();
        catalogue_json(&self.map_table(), &places)
    }
}

/// The area part of a place name: the text before the first separator
/// ("Besaid - Valley" → "Besaid").
#[must_use]
pub fn area_part(place: &str) -> &str {
    let cut = PLACE_SEPARATORS
        .iter()
        .filter_map(|s| place.find(s).filter(|&i| i > 0))
        .min();
    cut.map_or(place, |c| &place[..c]).trim()
}

/// The most common value of `items` (ties: the first seen).
fn most_common<'a>(items: impl Iterator<Item = &'a str>) -> Option<&'a str> {
    let mut counts: Vec<(&str, usize)> = Vec::new();
    for i in items {
        match counts.iter_mut().find(|(s, _)| *s == i) {
            Some((_, n)) => *n += 1,
            None => counts.push((i, 1)),
        }
    }
    let best = counts.iter().map(|&(_, n)| n).max()?;
    counts.into_iter().find(|&(_, n)| n == best).map(|(s, _)| s)
}

/// The catalogue as JSON: `languages`, `events` (in `eventid.bin`),
/// `areas` (`code`, `codes`, `title` per language), `levels` (`kind`,
/// `key`, `id`, `level`, `code`, `area`, `model_3d`, `model_2d`, `place`
/// per language: its first event's with a place, `events`: `name`, `id`,
/// `map`, `map_index`, `entrances`, `place_index`, `place` per language,
/// `script`) and `skipped` (`map`, `name`, `reason`).
///
/// Areas as ffx-remix groups them: an area code's title is the most common
/// [`area_part`] of its field events' places;
/// codes with the same title are one area (`bsil` and `bsyt`, both
/// "Besaid"), its code the first; codes without any place form `other`,
/// last. The titles that group are the English ones when `places` has
/// them (in Japanese Kilika's port and temple differ), else the first
/// language's. Each area's titles in the other languages are the most common area
/// part of its events' places in that language.
#[must_use]
pub fn catalogue_json(table: &Table, places: &[(&str, Vec<String>)]) -> Value {
    let place_of = |lang: usize, e: &Event| -> Option<&str> {
        let i = usize::from(e.place_index?);
        places
            .get(lang)?
            .1
            .get(i)
            .map(String::as_str)
            .filter(|s| !s.is_empty())
    };
    let (area_json, area_codes) = group_areas(table, places);
    let area_of = |code: &str| -> String {
        area_codes
            .iter()
            .find(|(c, _)| c == code)
            .map_or_else(|| "other".to_owned(), |(_, a)| a.clone())
    };
    let per_lang = |f: &dyn Fn(usize) -> Option<String>| -> Value {
        Value::Object(
            places
                .iter()
                .enumerate()
                .filter_map(|(li, (lang, _))| Some(((*lang).to_owned(), json!(f(li)?))))
                .collect(),
        )
    };
    let levels: Vec<Value> = table
        .levels
        .iter()
        .map(|l| {
            let events: Vec<Value> = l
                .events
                .iter()
                .map(|e| {
                    json!({
                        "name": e.name,
                        "id": ids::map_id(&e.name),
                        "map": e.map,
                        "map_index": e.map_index,
                        "entrances": e.entrances,
                        "place_index": e.place_index,
                        "place": per_lang(&|li| place_of(li, e).map(str::to_owned)),
                        "script": e.script,
                    })
                })
                .collect();
            let place = per_lang(&|li| {
                l.events
                    .iter()
                    .find_map(|e| place_of(li, e))
                    .map(str::to_owned)
            });
            json!({
                "kind": l.kind,
                "key": l.key,
                "id": ids::level_id(&l.level),
                "level": l.level,
                "code": l.code,
                "area": area_of(&l.code),
                "model_3d": l.model_3d,
                "model_2d": l.model_2d,
                "model_id": l.model_3d.as_deref().map(ids::model_id),
                "place": place,
                "events": events,
            })
        })
        .collect();
    let skipped: Vec<Value> = table
        .skipped
        .iter()
        .map(|s| json!({"map": s.map, "name": s.name, "reason": s.reason}))
        .collect();
    json!({
        "languages": places.iter().map(|(l, _)| *l).collect::<Vec<_>>(),
        "events": table.events,
        "areas": area_json,
        "levels": levels,
        "skipped": skipped,
    })
}

/// The areas ([`catalogue_json`]'s `areas`) and each area code's area.
fn group_areas(
    table: &Table,
    places: &[(&str, Vec<String>)],
) -> (Vec<Value>, Vec<(String, String)>) {
    let place_of = |lang: usize, e: &Event| -> Option<&str> {
        let i = usize::from(e.place_index?);
        places
            .get(lang)?
            .1
            .get(i)
            .map(String::as_str)
            .filter(|s| !s.is_empty())
    };
    // areas: by the English titles (as ffx-remix groups them), else the first language's
    let group = places.iter().position(|(l, _)| *l == "us").unwrap_or(0);
    let mut code_order: Vec<&str> = Vec::new();
    for l in &table.levels {
        if !code_order.contains(&l.code.as_str()) {
            code_order.push(&l.code);
        }
    }
    let mut areas: Vec<(String, Vec<&str>)> = Vec::new(); // (title, codes)
    let mut other: Vec<&str> = Vec::new();
    for &code in &code_order {
        let title = most_common(
            table
                .levels
                .iter()
                .filter(|l| l.code == code)
                .flat_map(|l| &l.events)
                .filter_map(|e| place_of(group, e))
                .map(area_part),
        );
        match title {
            Some(t) => match areas.iter_mut().find(|(x, _)| x == t) {
                Some((_, codes)) => codes.push(code),
                None => areas.push((t.to_owned(), vec![code])),
            },
            None => other.push(code),
        }
    }
    let mut area_json: Vec<Value> = areas
        .iter()
        .map(|(_, codes)| {
            let titles: serde_json::Map<String, Value> = places
                .iter()
                .enumerate()
                .filter_map(|(li, (lang, _))| {
                    let t = most_common(
                        table
                            .levels
                            .iter()
                            .filter(|l| codes.contains(&l.code.as_str()))
                            .flat_map(|l| &l.events)
                            .filter_map(|e| place_of(li, e))
                            .map(area_part),
                    )?;
                    Some(((*lang).to_owned(), json!(t)))
                })
                .collect();
            json!({"code": codes[0], "codes": codes, "title": titles})
        })
        .collect();
    if !other.is_empty() {
        area_json.push(json!({"code": "other", "codes": other, "title": {}}));
    }
    let mut codes: Vec<(String, String)> = Vec::new();
    for (_, group) in &areas {
        codes.extend(group.iter().map(|c| ((*c).to_owned(), group[0].to_owned())));
    }
    (area_json, codes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn level_folders_and_paths() {
        assert_eq!(
            level_folder("host0:/ffx/master/jppc/map/kami/kami01/bin/mapout.txa").as_deref(),
            Some("map/kami/kami01")
        );
        assert_eq!(level_folder("host0:/ffx/master/jppc/event/x.ebp"), None);
        assert_eq!(
            archive_path("host0:/ffx/master/jppc/map/A/b.vpa"),
            "ffx_ps2/ffx/master/jppc/map/a/b.vpa"
        );
    }

    #[test]
    fn script_header_fields() {
        // "EV", header at 0x10: entrance table 0x40..0x80 (2 entries), place 7
        let mut ebp = vec![0u8; 0x100];
        ebp[0..2].copy_from_slice(b"EV");
        ebp[4..8].copy_from_slice(&0x10u32.to_le_bytes());
        ebp[0x14..0x18].copy_from_slice(&0x40u32.to_le_bytes());
        ebp[0x18..0x1c].copy_from_slice(&0x80u32.to_le_bytes());
        ebp[0x2e..0x30].copy_from_slice(&7u16.to_le_bytes());
        ebp[0x50..0x52].copy_from_slice(&301u16.to_le_bytes());
        assert_eq!(script_header(&ebp), Some((2, Some(301), 7)));
        // bit 15: the place comes from the table at +0x28
        ebp[0x2e..0x30].copy_from_slice(&0x8000u16.to_le_bytes());
        ebp[0x38..0x3c].copy_from_slice(&0xc0u32.to_le_bytes());
        ebp[0xd0..0xd2].copy_from_slice(&42u16.to_le_bytes());
        assert_eq!(script_header(&ebp), Some((2, Some(301), 42)));
        assert_eq!(script_header(b"XX"), None);
    }

    #[test]
    fn areas_group_by_title() {
        assert_eq!(area_part("Besaid - Beach"), "Besaid");
        assert_eq!(area_part("ビサイド島・海岸"), "ビサイド島");
        assert_eq!(area_part("Luca"), "Luca");
        let ev = |name: &str, map, place| Event {
            name: name.into(),
            map,
            map_index: 1,
            entrances: 1,
            place_index: place,
            script: String::new(),
        };
        let lv = |key: &str, code: &str, events| Level {
            kind: "field",
            level: format!("/map/{code}/{key}/"),
            key: key.into(),
            code: code.into(),
            model_3d: None,
            model_2d: None,
            events,
        };
        let table = Table {
            levels: vec![
                lv("bsil00", "bsil", vec![ev("bsil0000", 70, Some(1))]),
                lv("bsyt00", "bsyt", vec![ev("bsyt0000", 71, Some(2))]),
                lv("test00", "test", vec![ev("test0000", 72, None)]),
            ],
            skipped: Vec::new(),
            events: 3,
        };
        let us = vec![
            "None".to_owned(),
            "Besaid - Beach".to_owned(),
            "Besaid - Temple".to_owned(),
        ];
        let c = catalogue_json(&table, &[("us", us)]);
        assert_eq!(c["areas"][0]["codes"], json!(["bsil", "bsyt"]));
        assert_eq!(c["areas"][0]["title"]["us"], "Besaid");
        assert_eq!(c["areas"][1]["code"], "other");
        assert_eq!(c["levels"][1]["area"], "bsil");
        assert_eq!(c["levels"][0]["place"]["us"], "Besaid - Beach");
        assert_eq!(c["levels"][0]["events"][0]["id"], "x:map/bsil0000");
        assert_eq!(c["levels"][2]["place"], json!({}));
        assert_eq!(c["levels"][2]["area"], "other");
    }

    /// On the player's game (skips without `FFX_GAME_DIR`): the counts and
    /// a few known maps (the Steam copy, 2026-11-17).
    #[test]
    fn catalogue_from_the_players_archive() {
        let Some(dir) = std::env::var_os("FFX_GAME_DIR") else {
            eprintln!("FFX_GAME_DIR is not set; skipping");
            return;
        };
        let imp = Importer::open(std::path::Path::new(&dir)).unwrap();
        let c = imp.map_catalogue(&[]);
        let levels = c["levels"].as_array().unwrap();
        let field: Vec<&Value> = levels.iter().filter(|l| l["kind"] == "field").collect();
        let events: usize = field
            .iter()
            .map(|l| l["events"].as_array().unwrap().len())
            .sum();
        assert_eq!(field.len(), 279);
        assert_eq!(events, 329);
        assert_eq!(levels.len() - field.len(), 56, "battle stages");
        assert_eq!(c["areas"].as_array().unwrap().len(), 32);
        assert_eq!(c["languages"].as_array().unwrap().len(), 8);
        let level = |key: &str| levels.iter().find(|l| l["key"] == key).unwrap();
        // Besaid beach: the save 7 map (0x46), its 3D model and 2D layers
        let bsil00 = level("bsil00");
        assert_eq!(bsil00["level"], "/map/bsil/bsil00/");
        assert_eq!(bsil00["id"], "x:level/bsil00");
        assert_eq!(bsil00["area"], "bsil");
        assert_eq!(bsil00["events"][0]["name"], "bsil0000");
        assert_eq!(bsil00["events"][0]["id"], "x:map/bsil0000");
        assert_eq!(bsil00["events"][0]["map"], 0x46);
        assert_eq!(bsil00["place"]["us"], "Besaid - Port");
        assert_eq!(bsil00["place"]["jp"], "ビサイド島・船着場");
        assert_eq!(bsil00["place"]["de"], "Besaid-Insel - Anlegestelle");
        assert_eq!(
            bsil00["model_id"],
            "x:/ffx_data/gamedata/ps3data/map/bsil/bsil00/mdl/d3d11/bsil00.dae.phyre"
        );
        assert!(bsil00["model_2d"].is_string());
        assert_eq!(level("cdsp00")["place"]["us"], "Salvage Ship - Deck");
        // 2D layers only
        let kami01 = level("kami01");
        assert!(kami01["model_3d"].is_null() && kami01["model_2d"].is_string());
        assert_eq!(kami01["place"]["us"], "Thunder Plains - Agency");
        // a battle stage joins its code's area
        let stage = level("bika00_a");
        assert_eq!(stage["kind"], "battle");
        assert_eq!(stage["level"], "/btlmap/bika/bika00_a/");
        let besaid = &c["areas"][0];
        assert_eq!(besaid["codes"], json!(["bsil", "bsyt"]));
        assert_eq!(besaid["title"]["us"], "Besaid");
        let last = c["areas"].as_array().unwrap().last().unwrap();
        assert_eq!(last["code"], "other");
    }
}
