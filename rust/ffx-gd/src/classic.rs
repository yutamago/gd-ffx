//! Classic content on Godot's side (docs/importer.md): the game's own maps
//! and characters imported from the player's archive by `ffx-import`, on
//! background threads, into a cache folder of gd-ffx's own. Godot asks for
//! one ([`Classic::request`]) and picks the finished ones up each frame
//! ([`Classic::finished`]), then loads the `.glb`.

use std::collections::HashSet;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, PoisonError};

use ffx_import::Importer;

/// What is imported.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Kind {
    /// A map model, by the scene's level name (`/map/bsil/bsil00/`).
    Map,
    /// A character model, by its name (`c001`).
    Character,
}

impl Kind {
    /// From Godot's string (`map`, `chr`).
    #[must_use]
    pub fn parse(s: &str) -> Option<Self> {
        match s {
            "map" => Some(Self::Map),
            "chr" => Some(Self::Character),
            _ => None,
        }
    }

    /// As Godot's string.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            Self::Map => "map",
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

/// The importer, its cache and the imports under way.
#[derive(Default)]
pub struct Classic {
    open: Mutex<Option<Arc<Open>>>,
    asked: Mutex<HashSet<(Kind, String)>>,
    done: Arc<Mutex<Vec<Done>>>,
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
