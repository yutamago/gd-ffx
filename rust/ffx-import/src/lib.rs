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
    let map = match parts.as_slice() {
        ["map", _, map, ..] => (*map).to_string(),
        [one] if one.len() >= 6 => one[..6].to_string(),
        _ => return None,
    };
    if map.len() < 4 || !map.chars().all(|c| c.is_ascii_alphanumeric() || c == '_') {
        return None;
    }
    let area = &map[..4];
    Some(format!(
        "{PS3DATA}/map/{area}/{map}/mdl/d3d11/{map}.dae.phyre"
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
}

impl std::fmt::Debug for Importer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Importer")
            .field("entries", &self.archive.entries().len())
            .finish()
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
        Ok(Self { archive })
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
        let path = map_model_path(level).ok_or_else(|| Error::Missing(level.to_string()))?;
        let name = file_stem(&path);
        self.import(&path, &cache.join("map").join(format!("{name}.glb")))
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
