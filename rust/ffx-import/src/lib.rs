//! The classic importer (docs/importer.md): the game's own maps and
//! characters, read from the player's `FFX_Data.vbf`, as glTF binaries
//! (`.glb`) that Godot loads at run time. Built on ffx-rust's readers:
//! `ffx-vbf` (the archive) and `ffx-phyre` (`PhyreEngine` models and
//! textures, `Model::to_glb_with`).
//!
//! Everything runs on the player's machine from the player's copy; the
//! output is a cache of gd-ffx's own (Godot's user folder), never shipped
//! and never committed.
//!
//! Spaces: a map model is written as stored (y up); the game places its
//! instances at the file's node worlds times 10 in its render space, which
//! is Godot's (ffx-rust's docs/state-api.md, "Spaces"), so Godot scales the
//! map by [`MAP_SCALE`]. A character model is written as stored too (y
//! down, the game's character space, in which its bones are posed): Godot
//! turns it half a turn about z under the character's root
//! (`GameStateView`).

use std::collections::HashMap;
use std::path::{Path, PathBuf};

/// The game places a 3D map's instances at the file's node worlds times
/// this (`PhyreScene_doOnFieldMapAssetLoaded`; ffx-rust's
/// docs/formats/scene.md).
pub const MAP_SCALE: f32 = 10.0;

/// The archive's prefix of the remaster's data.
const PS3DATA: &str = "ffx_data/gamedata/ps3data";

/// What can go wrong.
#[derive(Debug)]
pub enum Error {
    /// Reading the archive or writing the output.
    Io(std::io::Error),
    /// The archive: not found, unreadable.
    Archive(String),
    /// No such model in the archive.
    Missing(String),
    /// A model that does not parse.
    Model(String),
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(e) => write!(f, "io: {e}"),
            Self::Archive(e) => write!(f, "archive: {e}"),
            Self::Missing(p) => write!(f, "not in the archive: {p}"),
            Self::Model(e) => write!(f, "model: {e}"),
        }
    }
}

impl std::error::Error for Error {}

impl From<std::io::Error> for Error {
    fn from(e: std::io::Error) -> Self {
        Self::Io(e)
    }
}

/// Results.
pub type Result<T> = std::result::Result<T, Error>;

/// A level name as the game's scene holds it (`/map/znkd/znkd06/`, or an
/// event name's map such as `bsil0000`) as the archive path of its map
/// model: `.../map/<area>/<map>/mdl/d3d11/<map>.dae.phyre`. `None` when it
/// names no map.
#[must_use]
pub fn map_model_path(level: &str) -> Option<String> {
    let l = level.trim().trim_matches('/').to_ascii_lowercase();
    let parts: Vec<&str> = l.split('/').filter(|p| !p.is_empty()).collect();
    // `/map/<area>/<map>/` (the field), `/btlmap/<area>/<map>/` (a battle)
    let (root, map) = match parts.as_slice() {
        [root @ ("map" | "btlmap"), _, map, ..] => (*root, (*map).to_string()),
        [one] if one.len() >= 6 => ("map", one[..6].to_string()),
        _ => return None,
    };
    if map.len() < 4 || !map.chars().all(|c| c.is_ascii_alphanumeric() || c == '_') {
        return None;
    }
    let area = &map[..4];
    Some(format!(
        "{PS3DATA}/{root}/{area}/{map}/mdl/d3d11/{map}.dae.phyre"
    ))
}

/// A character model's name (`c001`, `m009`, `w041`...) as the archive
/// path of its model: `.../chr/<kind>/<name>/mdl/d3d11/<name>.dae.phyre`.
#[must_use]
pub fn character_model_path(model: &str) -> Option<String> {
    let m = model.trim().to_ascii_lowercase();
    if m.len() < 2 || !m.chars().all(|c| c.is_ascii_alphanumeric() || c == '_') {
        return None;
    }
    let kind = match m.as_bytes()[0] {
        b'c' => "pc",
        b'm' => "mon",
        b'n' => "npc",
        b's' => "sum",
        b'w' => "wep",
        b'f' => "obj",
        b'k' => "skl",
        _ => return None,
    };
    Some(format!("{PS3DATA}/chr/{kind}/{m}/mdl/d3d11/{m}.dae.phyre"))
}

/// The player's archive, open.
pub struct Importer {
    archive: ffx_vbf::Archive,
    /// Kernel tables read so far, by archive path.
    kernel: std::sync::Mutex<HashMap<String, Option<std::sync::Arc<ffx_kernel::Table>>>>,
}

impl std::fmt::Debug for Importer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Importer")
            .field("entries", &self.archive.entries().len())
            .finish_non_exhaustive()
    }
}

impl Importer {
    /// Opens `game_dir`'s `data/FFX_Data.vbf` (only reads it).
    ///
    /// # Errors
    /// When the archive is missing or unreadable.
    pub fn open(game_dir: &Path) -> Result<Self> {
        let path = game_dir.join("data").join("FFX_Data.vbf");
        let archive = ffx_vbf::Archive::open(&path)
            .map_err(|e| Error::Archive(format!("{}: {e}", path.display())))?;
        Ok(Self {
            archive,
            kernel: std::sync::Mutex::default(),
        })
    }

    /// The archive's paths containing `needle` (lower case), sorted.
    #[must_use]
    pub fn list(&self, needle: &str) -> Vec<String> {
        let needle = needle.to_ascii_lowercase();
        let mut v: Vec<String> = self
            .archive
            .entries()
            .iter()
            .map(|e| e.path.to_ascii_lowercase())
            .filter(|p| p.contains(&needle))
            .collect();
        v.sort();
        v
    }

    /// Whether the archive holds `path`.
    #[must_use]
    pub fn has(&self, path: &str) -> bool {
        self.archive.find(path).is_some()
    }

    /// A file of the archive.
    ///
    /// # Errors
    /// When it is not there or cannot be read.
    pub fn read(&self, path: &str) -> Result<Vec<u8>> {
        let entry = self
            .archive
            .find(path)
            .ok_or_else(|| Error::Missing(path.to_string()))?;
        self.archive
            .read(entry)
            .map_err(|e| Error::Archive(format!("{path}: {e}")))
    }

    /// The game's character set for `language` (`us`, `jp`, `de`...; the
    /// archive's table), to decode the game's text
    /// (`ffx_text::Charset::decode`).
    ///
    /// # Errors
    /// An unknown language, or a missing or bad table.
    pub fn charset(&self, language: &str) -> Result<ffx_text::Charset> {
        let lang = ffx_text::Language::from_code(language)
            .ok_or_else(|| Error::Missing(format!("language {language}")))?;
        let path = lang.charset_path();
        let table = self.read(&path)?;
        ffx_text::Charset::from_table(&table, lang)
            .map_err(|_| Error::Model(format!("{path}: not a character table")))
    }

    /// The name of the battle command, item or monster ability `id` (the
    /// battle menu's ids: `id >> 12` 2 an item, 3 a command, 4 and 6 a
    /// monster's; the low 12 bits the record) in `language`, encoded (the
    /// kernel tables' first string; docs/formats/kernel.md in ffx-rust);
    /// `None` for an id of another kind or without a record.
    #[must_use]
    pub fn kernel_name(&self, language: &str, id: u16) -> Option<Vec<u8>> {
        let table = match id >> 12 {
            2 => "item",
            3 => "command",
            4 => "monmagic1",
            6 => "monmagic2",
            _ => return None,
        };
        let lang = ffx_text::Language::from_code(language)?;
        let path = format!("{}{table}.bin", lang.kernel_dir());
        let t = {
            let mut cache = self
                .kernel
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            cache
                .entry(path.clone())
                .or_insert_with(|| {
                    self.read(&path)
                        .ok()
                        .and_then(|b| ffx_kernel::Table::parse(&b).ok())
                        .map(std::sync::Arc::new)
                })
                .clone()?
        };
        t.string(id & 0x0fff, 0).map(<[u8]>::to_vec)
    }

    /// The name of sphere grid node kind `kind` (a node's `+6`) in
    /// `language`, encoded: the first string of record `kind` of the
    /// kernel's `panel.bin` (`g_AbmapNodeKinds`, which `eiAbmLoad` loads for
    /// the grid); `None` without the table or the record.
    #[must_use]
    pub fn grid_node_name(&self, language: &str, kind: u16) -> Option<Vec<u8>> {
        let lang = ffx_text::Language::from_code(language)?;
        let path = format!("{}panel.bin", lang.kernel_dir());
        let t = ffx_kernel::Table::parse(&self.read(&path).ok()?).ok()?;
        t.string(kind, 0).map(<[u8]>::to_vec)
    }

    /// The save list's place name `index` (a save's header `+0x18`,
    /// `AtelGetSaveDic`) in `language`, encoded: as `AtelGetSaveDicName`
    /// reads it (ffx-rust's `ffx-script` `atel_get_save_dic_name`) from the
    /// macro dictionary (`menu/macrodic.dcp`, 16 section offsets;
    /// `TOGetSaveDicAdrs` takes section 11, `+0x2c`): the section's first
    /// `u16` / 4 entries, entry `i` the text at the section plus its
    /// `u16` `2 i` (`2 i + 1` is the hiragana form), up to a 0; an index past
    /// the count gives entry 0. `None` without the file.
    #[must_use]
    pub fn place_name(&self, language: &str, index: u16) -> Option<Vec<u8>> {
        let lang = ffx_text::Language::from_code(language)?;
        let path = format!("ffx_ps2/ffx/master/new_{}pc/menu/macrodic.dcp", lang.code());
        let b = self.read(&path).ok()?;
        place_name_in(&b, index)
    }

    /// A material's texture as PNG: `path` as the model stores it (the
    /// glTF's `extras.ffx_textures`, e.g.
    /// `PS3Data/map/bsil/bsil00/tex/reflection3.dds`), face `face` (0 for a
    /// 2D texture, 0..6 for a cube map: D3D's +x, -x, +y, -y, +z, -z).
    /// `None` when the texture is missing or has no such face.
    #[must_use]
    pub fn texture_png(&self, path: &str, face: usize) -> Option<Vec<u8>> {
        let t = ffx_phyre::MaterialTexture {
            sampler: String::new(),
            path: path.to_string(),
        };
        let tex = ffx_phyre::Texture::parse(self.read(&t.archive_path()).ok()?).ok()?;
        tex.face_png(face)
    }

    /// A texture by the name the game holds at run time (an effect
    /// segment's, e.g. `../../../FFX_Data/GameData/PS3Data/map/bsil/bsil00/fp/tex/D3D11/x.dds.phyre`):
    /// its top level as PNG; `None` when it is not in the archive.
    #[must_use]
    pub fn runtime_texture_png(&self, name: &str) -> Option<Vec<u8>> {
        let lower = name.replace('\\', "/").to_ascii_lowercase();
        let at = lower.find("ffx_data/")?;
        let tex = ffx_phyre::Texture::parse(self.read(&lower[at..]).ok()?).ok()?;
        Some(tex.to_png())
    }

    /// Whether the texture at `path` (as [`Importer::texture_png`]) is a cube map.
    #[must_use]
    pub fn texture_is_cube(&self, path: &str) -> bool {
        let t = ffx_phyre::MaterialTexture {
            sampler: String::new(),
            path: path.to_string(),
        };
        self.read(&t.archive_path())
            .ok()
            .and_then(|b| ffx_phyre::Texture::parse(b).ok())
            .is_some_and(|t| t.kind.faces() == 6)
    }

    /// The model at `path` as a glTF binary with its base colour textures
    /// embedded (PNG), as stored (no axis change: see the module's doc).
    ///
    /// # Errors
    /// When the model is missing or does not parse.
    pub fn model_glb(&self, path: &str) -> Result<Vec<u8>> {
        let bytes = self.read(path)?;
        let model =
            ffx_phyre::Model::parse(&bytes).map_err(|e| Error::Model(format!("{path}: {e}")))?;
        let mut pngs: HashMap<String, Option<Vec<u8>>> = HashMap::new();
        Ok(model.to_glb_with(
            false,
            |t| {
                pngs.entry(t.archive_path())
                    .or_insert_with_key(|p| {
                        let entry = self.archive.find(p)?;
                        let texture =
                            ffx_phyre::Texture::parse(self.archive.read(entry).ok()?).ok()?;
                        Some(texture.to_png())
                    })
                    .clone()
            },
            &[],
        ))
    }

    /// Writes the model at `path` to `out` (made with its folders) unless
    /// `out` is there already; returns `out`.
    ///
    /// # Errors
    /// As [`Importer::model_glb`], and writing.
    pub fn import(&self, path: &str, out: &Path) -> Result<PathBuf> {
        if out.is_file() {
            return Ok(out.to_path_buf());
        }
        let glb = self.model_glb(path)?;
        if let Some(dir) = out.parent() {
            std::fs::create_dir_all(dir)?;
        }
        // written beside, then moved: a reader never sees half a file
        let tmp = out.with_extension("glb.part");
        std::fs::write(&tmp, glb)?;
        std::fs::rename(&tmp, out)?;
        Ok(out.to_path_buf())
    }

    /// The map model of `level` imported under `cache`
    /// (`<cache>/map/<map>.glb`).
    ///
    /// # Errors
    /// When `level` names no map, or as [`Importer::import`].
    pub fn import_map(&self, level: &str, cache: &Path) -> Result<PathBuf> {
        let path = self
            .map_model(level)
            .ok_or_else(|| Error::Missing(level.to_string()))?;
        let name = file_stem(&path);
        self.import(&path, &cache.join("map").join(format!("{name}.glb")))
    }

    /// The archive path of `level`'s map model: [`map_model_path`], or for
    /// the maps the remaster keeps under `2d/` (`.../<map>/2d/mdl/d3d11/`,
    /// with `2d/tex/`: `ptkl14`, `mihn03`, `guad01`, `kami01`, `maca01`,
    /// `bvyt04`, `mcfr11`, `nagi07`, `sins00`, found by gd-ffx's sweep of
    /// the developer saves) that one
    /// when the first is not in the archive.
    #[must_use]
    pub fn map_model(&self, level: &str) -> Option<String> {
        let path = map_model_path(level)?;
        if self.has(&path) {
            return Some(path);
        }
        let flat = path.replacen("/mdl/d3d11/", "/2d/mdl/d3d11/", 1);
        Some(if self.has(&flat) { flat } else { path })
    }

    /// The character model `model` imported under `cache`
    /// (`<cache>/chr/<model>.glb`).
    ///
    /// # Errors
    /// When `model` names no character, or as [`Importer::import`].
    pub fn import_character(&self, model: &str, cache: &Path) -> Result<PathBuf> {
        let path = character_model_path(model).ok_or_else(|| Error::Missing(model.to_string()))?;
        let name = file_stem(&path);
        self.import(&path, &cache.join("chr").join(format!("{name}.glb")))
    }
}

/// `.../<name>.dae.phyre`'s name.
fn file_stem(path: &str) -> String {
    let file = path.rsplit('/').next().unwrap_or(path);
    file.strip_suffix(".dae.phyre").unwrap_or(file).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn level_names_as_map_models() {
        assert_eq!(
            map_model_path("/map/znkd/znkd06/").as_deref(),
            Some("ffx_data/gamedata/ps3data/map/znkd/znkd06/mdl/d3d11/znkd06.dae.phyre")
        );
        assert_eq!(
            map_model_path("bsil0000").as_deref(),
            Some("ffx_data/gamedata/ps3data/map/bsil/bsil00/mdl/d3d11/bsil00.dae.phyre")
        );
        assert_eq!(
            map_model_path("/btlmap/cdsp/cdsp00_a/").as_deref(),
            Some("ffx_data/gamedata/ps3data/btlmap/cdsp/cdsp00_a/mdl/d3d11/cdsp00_a.dae.phyre")
        );
        assert_eq!(map_model_path(""), None);
        assert_eq!(map_model_path("/map/../x/"), None);
    }

    #[test]
    fn character_names_as_models() {
        assert_eq!(
            character_model_path("c001").as_deref(),
            Some("ffx_data/gamedata/ps3data/chr/pc/c001/mdl/d3d11/c001.dae.phyre")
        );
        assert_eq!(
            character_model_path("w041").as_deref(),
            Some("ffx_data/gamedata/ps3data/chr/wep/w041/mdl/d3d11/w041.dae.phyre")
        );
        assert_eq!(character_model_path("x001"), None);
        assert_eq!(character_model_path("c0/1"), None);
    }

    /// On the player's game (skips without `FFX_GAME_DIR`): the opening's
    /// map and Tidus import.
    #[test]
    fn imports_from_the_players_archive() {
        let Some(dir) = std::env::var_os("FFX_GAME_DIR") else {
            eprintln!("FFX_GAME_DIR is not set; skipping");
            return;
        };
        let imp = Importer::open(Path::new(&dir)).unwrap();
        let cache = std::env::temp_dir().join(format!("ffx-import-test-{}", std::process::id()));
        // the English character set decodes plain letters
        let cs = imp.charset("us").unwrap();
        assert_ne!(cs.decode(&[0x30, 0x31]), "");
        // the first battle command is Attack
        let attack = imp.kernel_name("us", 0x3000).unwrap();
        assert_eq!(cs.decode(&attack), "Attack");
        let map = imp.import_map("/map/znkd/znkd06/", &cache).unwrap();
        let chr = imp.import_character("c001", &cache).unwrap();
        for p in [&map, &chr] {
            let b = std::fs::read(p).unwrap();
            assert_eq!(&b[0..4], b"glTF", "{}", p.display());
            assert!(b.len() > 10_000, "{}: {} bytes", p.display(), b.len());
        }
        std::fs::remove_dir_all(&cache).unwrap();
    }
}

/// [`Importer::place_name`] on the dictionary's bytes.
#[must_use]
pub fn place_name_in(dic: &[u8], index: u16) -> Option<Vec<u8>> {
    const SECTION: usize = 11;
    let u32_at = |o: usize| {
        dic.get(o..o + 4)
            .map(|s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
    };
    let u16_at = |o: usize| dic.get(o..o + 2).map(|s| u16::from_le_bytes([s[0], s[1]]));
    let sec = usize::try_from(u32_at(SECTION * 4)?).ok()?;
    if sec == 0 {
        return None;
    }
    let count = usize::from(u16_at(sec)? >> 2);
    let i = if usize::from(index) >= count {
        0
    } else {
        usize::from(index)
    };
    let at = sec + usize::from(u16_at(sec + 4 * i)?);
    let text = dic.get(at..)?;
    let end = text.iter().position(|&c| c == 0).unwrap_or(text.len());
    Some(text[..end].to_vec())
}

#[cfg(test)]
mod place_tests {
    use super::place_name_in;

    /// A made-up dictionary: section 11 with two entries (each a name and
    /// its hiragana form).
    #[test]
    fn place_names_come_from_section_11() {
        let sec = 0x40_usize;
        let mut d = vec![0u8; sec];
        d[44..48].copy_from_slice(&u32::try_from(sec).unwrap().to_le_bytes());
        // 2 entries: 4 u16 offsets (count = first / 4 = 8 / 4 = 2)
        let offs: [u16; 4] = [8, 11, 11, 14];
        for o in offs {
            d.extend_from_slice(&o.to_le_bytes());
        }
        d.extend_from_slice(&[0x41, 0x42, 0, 0x43, 0x44, 0, 0x45, 0]);
        assert_eq!(place_name_in(&d, 0).as_deref(), Some(&[0x41, 0x42][..]));
        assert_eq!(place_name_in(&d, 1).as_deref(), Some(&[0x43, 0x44][..]));
        assert_eq!(
            place_name_in(&d, 9).as_deref(),
            Some(&[0x41, 0x42][..]),
            "past the count: entry 0"
        );
        assert_eq!(place_name_in(&[0u8; 8], 0), None);
    }
}
