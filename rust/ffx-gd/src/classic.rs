//! Classic content on Godot's side (docs/importer.md): the game's own maps
//! and characters imported from the player's archive by `ffx-import`, on
//! background threads, into a cache folder of gd-ffx's own. Godot asks for
//! one ([`Classic::request`]) and picks the finished ones up each frame
//! ([`Classic::finished`]), then loads the `.glb`.

use std::collections::HashSet;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, PoisonError};

use ffx_import::{Importer, Pixels};

/// What is imported.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Kind {
    /// A map model, by the scene's level name (`/map/bsil/bsil00/`).
    Map,
    /// A map's 2D layers, by the level name (`/map/kami/kami01/`).
    MapLayers,
    /// A character model, by its name (`c001`).
    Character,
}

impl Kind {
    /// From Godot's string (`map`, `chr`).
    #[must_use]
    pub fn parse(s: &str) -> Option<Self> {
        match s {
            "map" => Some(Self::Map),
            "map2d" => Some(Self::MapLayers),
            "chr" => Some(Self::Character),
            _ => None,
        }
    }

    /// As Godot's string.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            Self::Map => "map",
            Self::MapLayers => "map2d",
            Self::Character => "chr",
        }
    }
}

/// A finished import: the `.glb`, or why not.
#[derive(Debug, Clone)]
pub struct Done {
    /// What was asked.
    pub kind: Kind,
    /// Its name as asked.
    pub name: String,
    /// The `.glb`, or the error.
    pub result: Result<PathBuf, String>,
}

struct Open {
    importer: Arc<Importer>,
    cache: PathBuf,
}

/// The game's character set, once loaded ([`Classic::decode`]).
static CHARSET: Mutex<Option<(String, Arc<ffx_text::Charset>)>> = Mutex::new(None);

/// A texture asked for in the background ([`Classic::texture_request`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TextureAsk {
    /// A material texture's face, by the path the model stores.
    Face(String, usize),
    /// A texture by the name the game holds at run time.
    Runtime(String),
}

/// A texture read in the background: the caller's id and the pixels
/// (`None`: not in the archive, or no such face).
pub type TextureDone = (i64, Option<Pixels>);

/// The importer, its cache and the imports under way.
#[derive(Default)]
pub struct Classic {
    open: Mutex<Option<Arc<Open>>>,
    asked: Mutex<HashSet<(Kind, String)>>,
    done: Arc<Mutex<Vec<Done>>>,
    /// Textures read in the background, by the caller's id.
    textures: Arc<Mutex<Vec<TextureDone>>>,
}

fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(PoisonError::into_inner)
}

impl Classic {
    /// Opens the player's archive in `game_dir` (read only) with `cache` as
    /// the output folder.
    ///
    /// # Errors
    /// When the archive cannot be opened.
    pub fn open(&self, game_dir: &std::path::Path, cache: PathBuf) -> Result<(), String> {
        let importer = Importer::open(game_dir).map_err(|e| e.to_string())?;
        *lock(&self.open) = Some(Arc::new(Open {
            importer: Arc::new(importer),
            cache,
        }));
        Ok(())
    }

    /// The game's encoded `text` as UTF-8 in `language`'s character set
    /// (loaded from the archive on first use); `None` without the archive.
    #[must_use]
    pub fn decode(&self, language: &str, text: &[u8]) -> Option<String> {
        let open = lock(&self.open).clone()?;
        let mut cs = lock(&CHARSET);
        if cs.as_ref().is_none_or(|(l, _)| l != language) {
            let charset = open.importer.charset(language).ok()?;
            *cs = Some((language.to_string(), Arc::new(charset)));
        }
        cs.as_ref().map(|(_, c)| c.decode(text))
    }

    /// The battle command, item or monster ability `id`'s name in
    /// `language` as text; `None` without the archive or such a record.
    #[must_use]
    pub fn kernel_name(&self, language: &str, id: u16) -> Option<String> {
        let open = lock(&self.open).clone()?;
        let name = open.importer.kernel_name(language, id)?;
        self.decode(language, &name)
    }

    /// Sphere grid node kind `kind`'s name in `language`, decoded
    /// (`ffx_import::Importer::grid_node_name`).
    #[must_use]
    pub fn grid_node_name(&self, language: &str, kind: u16) -> Option<String> {
        let open = lock(&self.open).clone()?;
        let name = open.importer.grid_node_name(language, kind)?;
        self.decode(language, &name)
    }

    /// The save list's place name `index` in `language`, decoded
    /// (`ffx_import::Importer::place_name`).
    #[must_use]
    pub fn place_name(&self, language: &str, index: u16) -> Option<String> {
        let open = lock(&self.open).clone()?;
        let name = open.importer.place_name(language, index)?;
        self.decode(language, &name)
    }

    /// A piece of gear's name in `language`, decoded
    /// (`ffx_import::Importer::weapon_name`).
    #[must_use]
    pub fn weapon_name(
        &self,
        language: &str,
        name: u16,
        character: u8,
        kana: bool,
    ) -> Option<String> {
        let open = lock(&self.open).clone()?;
        let text = open.importer.weapon_name(language, name, character, kana)?;
        self.decode(language, &text)
    }

    /// Line `index` of the localisation kit in `language`, decoded
    /// (`ffx_import::Importer::loc_text`).
    #[must_use]
    pub fn loc_text(&self, language: &str, index: u32) -> Option<String> {
        let open = lock(&self.open).clone()?;
        let text = open.importer.loc_text(language, index)?;
        self.decode(language, &text)
    }

    /// A material texture's face as PNG (`ffx_import::Importer::texture_png`).
    #[must_use]
    pub fn texture_png(&self, path: &str, face: usize) -> Option<Vec<u8>> {
        let open = lock(&self.open).clone()?;
        open.importer.texture_png(path, face)
    }

    /// A texture by its run-time name as PNG (`ffx_import::Importer::runtime_texture_png`).
    #[must_use]
    pub fn runtime_texture_png(&self, name: &str) -> Option<Vec<u8>> {
        let open = lock(&self.open).clone()?;
        open.importer.runtime_texture_png(name)
    }

    /// A texture's pixels now (on the calling thread).
    #[must_use]
    pub fn texture_rgba(&self, ask: &TextureAsk) -> Option<Pixels> {
        let open = lock(&self.open).clone()?;
        match ask {
            TextureAsk::Face(path, face) => open.importer.texture_rgba(path, *face),
            TextureAsk::Runtime(name) => open.importer.runtime_texture_rgba(name),
        }
    }

    /// Reads a texture on a thread of its own (the archive read and the
    /// block decoding cost milliseconds; Godot's main thread should not
    /// wait for them); [`Classic::textures_finished`] hands it over with
    /// `id`. False when the archive is not open.
    pub fn texture_request(&self, id: i64, ask: TextureAsk) -> bool {
        let Some(open) = lock(&self.open).clone() else {
            return false;
        };
        let done = Arc::clone(&self.textures);
        std::thread::Builder::new()
            .name("ffx-texture".into())
            .spawn(move || {
                let px = match &ask {
                    TextureAsk::Face(path, face) => open.importer.texture_rgba(path, *face),
                    TextureAsk::Runtime(name) => open.importer.runtime_texture_rgba(name),
                };
                lock(&done).push((id, px));
            })
            .is_ok()
    }

    /// The textures read since the last call, by id (`None`: not in the
    /// archive, or no such face).
    #[must_use]
    pub fn textures_finished(&self) -> Vec<TextureDone> {
        std::mem::take(&mut *lock(&self.textures))
    }

    /// Whether a material texture is a cube map.
    #[must_use]
    pub fn texture_is_cube(&self, path: &str) -> bool {
        lock(&self.open)
            .clone()
            .is_some_and(|o| o.importer.texture_is_cube(path))
    }

    /// Which of `names` an effect variant switches on
    /// (`ffx_import::Importer::effect_switches`).
    #[must_use]
    pub fn effect_switches(&self, effect: &str, names: &[&str]) -> Vec<String> {
        lock(&self.open).clone().map_or_else(Vec::new, |o| {
            o.importer
                .effect_switches(effect, names)
                .into_iter()
                .map(str::to_string)
                .collect()
        })
    }

    /// Whether the archive is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        lock(&self.open).is_some()
    }

    /// Imports `name` on a thread of its own (once: a repeated ask while
    /// it runs or after it finished does nothing). False when the archive
    /// is not open.
    pub fn request(&self, kind: Kind, name: &str) -> bool {
        let Some(open) = lock(&self.open).clone() else {
            return false;
        };
        if !lock(&self.asked).insert((kind, name.to_string())) {
            return true;
        }
        let done = Arc::clone(&self.done);
        let name = name.to_string();
        let spawned = std::thread::Builder::new()
            .name(format!("ffx-import {}", kind.name()))
            .spawn(move || {
                let r = match kind {
                    Kind::Map => open.importer.import_map(&name, &open.cache),
                    Kind::MapLayers => open.importer.import_map_layers(&name, &open.cache),
                    Kind::Character => open.importer.import_character(&name, &open.cache),
                };
                lock(&done).push(Done {
                    kind,
                    name,
                    result: r.map_err(|e| e.to_string()),
                });
            });
        spawned.is_ok()
    }

    /// The imports finished since the last call.
    #[must_use]
    pub fn finished(&self) -> Vec<Done> {
        std::mem::take(&mut *lock(&self.done))
    }
}
