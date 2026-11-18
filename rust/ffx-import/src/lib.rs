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

use serde_json::{Value, json};

pub mod chars;
pub mod effect;
pub mod exits;
pub mod flash;
pub mod gamedata;
pub mod ids;
pub mod maps;
pub mod morph;

/// The version of what the importer writes into a `.glb`
/// (`asset.extras.ffx_import`): a cached import of another version is made
/// again. 2: the materials' effect variant states (`ffx_switches`,
/// `ffx_passes`, `ffx_inputs`, `ffx_input_types`), UV set bindings
/// (`ffx_texcoords`) and sampler address modes (`ffx_wrap`), the meshes'
/// `ffx_render_order`. 3: the meshes' instance (`ffx_instance`) and morph
/// fields (`ffx_obj_id`, `ffx_anim_ct`, `ffx_mime_ct`, `ffx_dobj_flag`), the
/// position morphs as glTF morph targets, the colour morphs' second colour
/// as `TEXCOORD_2`/`TEXCOORD_3`, the materials' `ffx_colors`
/// (docs/importer.md, "Morphing meshes"); the meshes' and primitives'
/// segment model ids (`ffx_model_id`). 4: a primitive whose own stream
/// bindings put an input on another `TEXCOORD_n` than its material's
/// `ffx_texcoords` says gets its attributes re-pointed to match
/// ([`align_texcoords`]). 5: a character's cloth segments (`ffx_cloth` and
/// a vertex number attribute, [`add_cloth`]). 6: stable part IDs
/// ([`ids`], [`add_ids`]): `ffx_id` on the nodes, meshes and primitives,
/// `ffx_texture_ids` on the materials, `asset.extras.ffx_model`.
pub const FORMAT: u64 = 6;

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
    /// The level has no model of that kind, as many do: a map of 2D layers
    /// only has no 3D model, most 3D maps have no 2D layers.
    None(String),
    /// A model that does not parse.
    Model(String),
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(e) => write!(f, "io: {e}"),
            Self::Archive(e) => write!(f, "archive: {e}"),
            Self::Missing(p) => write!(f, "not in the archive: {p}"),
            Self::None(p) => write!(f, "none: {p}"),
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
    /// Localisation kits read so far ([`Importer::loc_text`]), by archive path.
    lockit: std::sync::Mutex<HashMap<String, Option<std::sync::Arc<Vec<u8>>>>>,
    /// Effect variants read so far ([`effect::variant_extras`]), by import id.
    effects: std::sync::Mutex<HashMap<String, Option<Value>>>,
    /// The event names by map number ([`exits::event_names`]), read once.
    events: std::sync::OnceLock<Vec<String>>,
    /// Flash movies parsed so far ([`Importer::flash_movie`]), by name.
    flash: std::sync::Mutex<HashMap<String, Option<std::sync::Arc<flash::Movie>>>>,
    /// The map catalogue's table ([`Importer::map_table`]), read once.
    maps: std::sync::OnceLock<std::sync::Arc<maps::Table>>,
}

impl std::fmt::Debug for Importer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Importer")
            .field("entries", &self.archive.entries().len())
            .finish_non_exhaustive()
    }
}

/// A developer save (`Importer::dev_saves`): where its load starts.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DevSave {
    /// 1..=110, as `--devsave=N` and `request_devsave` take it.
    pub number: u16,
    /// The map number (`World.map`) its load jumps to.
    pub map: u16,
    pub entrance: u16,
    pub scenario: u16,
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
            lockit: std::sync::Mutex::default(),
            effects: std::sync::Mutex::default(),
            events: std::sync::OnceLock::new(),
            flash: std::sync::Mutex::default(),
            maps: std::sync::OnceLock::new(),
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

    /// The developers' saves (`savesforviewer/1..110`) the archive holds:
    /// each one's number, the map and entrance its load jumps to (the save
    /// data block at `+0x40`: `+0xba`, `+0xb8`) and its scenario
    /// (`+0xbec`; ffx-rust's docs/harness.md, "Developer saves").
    #[must_use]
    pub fn dev_saves(&self) -> Vec<DevSave> {
        (1..=110u16)
            .filter_map(|n| {
                let b = self
                    .read(&format!("ffx_data/gamedata/ps3data/savesforviewer/{n}"))
                    .ok()?;
                let u16_at = |o: usize| {
                    b.get(0x40 + o..0x40 + o + 2)
                        .map_or(0, |s| u16::from_le_bytes([s[0], s[1]]))
                };
                Some(DevSave {
                    number: n,
                    map: u16_at(0xba),
                    entrance: u16_at(0xb8),
                    scenario: u16_at(0xbec),
                })
            })
            .collect()
    }

    /// The Flash movie `name` (`chocobo.swf`, from
    /// `ffx_data/gamedata/ps3data/flash/`), parsed once; `None` when the
    /// archive lacks it or it does not parse.
    #[must_use]
    pub fn flash_movie(&self, name: &str) -> Option<std::sync::Arc<flash::Movie>> {
        let key = name.to_ascii_lowercase();
        let mut cache = self
            .flash
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        cache
            .entry(key.clone())
            .or_insert_with(|| {
                let data = self.read(&flash::movie_path(&key)).ok()?;
                flash::Movie::parse(&data).ok().map(std::sync::Arc::new)
            })
            .clone()
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
        let t = self.kernel_table(language, table)?;
        t.string(id & 0x0fff, 0).map(<[u8]>::to_vec)
    }

    /// The kernel table `table` (`item`, `w_name`...) of `language`, read
    /// once; `None` without it.
    pub(crate) fn kernel_table(
        &self,
        language: &str,
        table: &str,
    ) -> Option<std::sync::Arc<ffx_kernel::Table>> {
        let lang = ffx_text::Language::from_code(language)?;
        let path = format!("{}{table}.bin", lang.kernel_dir());
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
            .clone()
    }

    /// The name of a piece of gear in `language`, encoded, as
    /// `MsGetSaveWeaponName` gives it: `MsWeaponName(name, character,
    /// kana)` (`0x007a0c50`) on the gear's equipment record (`+0` the
    /// `w_name` record, chosen when the gear is made; `+4` its character,
    /// 0..6 the party): string `character` of the kernel's `w_name.bin`
    /// record `name & 0xfff`, or string `7 + character` (`+0x1c + 4 k`, the
    /// hiragana form) with `kana` (the config's hiragana bit, save
    /// `+0x3d0c` bit 3). A record outside the table is the first record,
    /// as `MsGetExcelData` gives it. `None` without the table.
    #[must_use]
    pub fn weapon_name(
        &self,
        language: &str,
        name: u16,
        character: u8,
        kana: bool,
    ) -> Option<Vec<u8>> {
        let t = self.kernel_table(language, "w_name")?;
        let id = name & 0x0fff;
        let id = if t.record(id).is_some() {
            id
        } else {
            t.ids().next()?
        };
        let field = usize::from(character) + if kana { 7 } else { 0 };
        t.string(id, field).map(<[u8]>::to_vec)
    }

    /// Line `index` of the remaster's localisation kit for `language`
    /// (`LocalizationManager_getText`, `0x006db1f0`; `SaveDataGetLoc`):
    /// `FFX_Data/GameData/PS3Data/LocKit/FFX_LOC_KIT_PS3_<LANG>.BIN`
    /// (`LocalizationManager_readLocalizationKit`), lines ending at `\n`,
    /// `\r` dropped, past the last line the last one. The bytes as stored
    /// (the game's encoding for most lines); `None` without the file.
    #[must_use]
    pub fn loc_text(&self, language: &str, index: u32) -> Option<Vec<u8>> {
        let lang = ffx_text::Language::from_code(language)?;
        let path = format!(
            "ffx_data/gamedata/ps3data/lockit/ffx_loc_kit_ps3_{}.bin",
            lang.code()
        );
        let b = {
            let mut cache = self
                .lockit
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            cache
                .entry(path.clone())
                .or_insert_with(|| self.read(&path).ok().map(std::sync::Arc::new))
                .clone()?
        };
        Some(loc_line(&b, index))
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

    /// A material's texture face as RGBA8 rows, top row first, as
    /// [`Importer::texture_png`] without the PNG: width, height, the
    /// texture's faces (6 for a cube map) and the pixels.
    #[must_use]
    pub fn texture_rgba(&self, path: &str, face: usize) -> Option<Pixels> {
        let t = ffx_phyre::MaterialTexture {
            sampler: String::new(),
            path: path.to_string(),
        };
        let tex = ffx_phyre::Texture::parse(self.read(&t.archive_path()).ok()?).ok()?;
        let rgba = tex.face_rgba(face)?;
        Some(Pixels {
            width: tex.width,
            height: tex.height,
            faces: tex.kind.faces(),
            rgba,
        })
    }

    /// A run-time texture's top level as RGBA8 rows, as
    /// [`Importer::runtime_texture_png`] without the PNG.
    #[must_use]
    pub fn runtime_texture_rgba(&self, name: &str) -> Option<Pixels> {
        let lower = name.replace('\\', "/").to_ascii_lowercase();
        let at = lower.find("ffx_data/")?;
        let tex = ffx_phyre::Texture::parse(self.read(&lower[at..]).ok()?).ok()?;
        Some(Pixels {
            width: tex.width,
            height: tex.height,
            faces: tex.kind.faces(),
            rgba: tex.to_rgba(),
        })
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

    /// Which of `names` a material's effect variant switches on
    /// (`extras.ffx_effect`, e.g.
    /// `PS3Data/Shaders/PhyrePrerenderedShader.fx#987968AD...`; the
    /// switches such as `MASK_DEPTH`, `ADDITIVE_BLENDING`;
    /// `PEffectVariant.m_switches`, read by [`Importer::effect_extras`]).
    /// Empty when the variant is not in the archive.
    #[must_use]
    pub fn effect_switches<'n>(&self, effect: &str, names: &[&'n str]) -> Vec<&'n str> {
        let Some(fx) = self.effect_extras(effect) else {
            return Vec::new();
        };
        names
            .iter()
            .filter(|n| fx["ffx_switches"].get(**n).is_some())
            .copied()
            .collect()
    }

    /// The extras of effect variant `effect` (a material's import id):
    /// [`effect::variant_extras`], read once; `None` when it is not in the
    /// archive or does not parse.
    #[must_use]
    pub fn effect_extras(&self, effect: &str) -> Option<Value> {
        let mut cache = self
            .effects
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        cache
            .entry(effect.to_ascii_lowercase())
            .or_insert_with(|| {
                let bytes = self.read(&effect::variant_path(effect)).ok()?;
                effect::variant_extras(bytes).ok()
            })
            .clone()
    }

    /// The stable part IDs ([`ids::ModelIds`]) of the model at `path`.
    ///
    /// # Errors
    /// When the model is missing or does not parse.
    pub fn model_ids(&self, path: &str) -> Result<ids::ModelIds> {
        let bytes = self.read(path)?;
        let model =
            ffx_phyre::Model::parse(&bytes).map_err(|e| Error::Model(format!("{path}: {e}")))?;
        Ok(ids::ModelIds::of(path, &model))
    }

    /// Which parts of the model at `path` sample which textures: per
    /// texture ID ([`ids::texture_id`]), its samplers and the part IDs
    /// whose segment's material (the instance's material set, else the
    /// segment's own) names it, in the model's order. Parsing only: no
    /// glTF, no pixels (the remaster's where-used index,
    /// docs/remaster-editor.md).
    ///
    /// # Errors
    /// When the model is missing or does not parse.
    pub fn texture_uses(&self, path: &str) -> Result<Vec<ids::TextureUse>> {
        let bytes = self.read(path)?;
        let model =
            ffx_phyre::Model::parse(&bytes).map_err(|e| Error::Model(format!("{path}: {e}")))?;
        Ok(ids::texture_uses(&model, &ids::ModelIds::of(path, &model)))
    }

    /// The model at `path` as a glTF binary with its base colour textures
    /// embedded (PNG), as stored (no axis change: see the module's doc),
    /// with gd-ffx's extras ([`Importer::add_extras`]).
    ///
    /// # Errors
    /// When the model is missing or does not parse.
    pub fn model_glb(&self, path: &str) -> Result<Vec<u8>> {
        self.model_glb_with(path, &[])
    }

    /// As [`Importer::model_glb`], with `animations` (glTF animations of the
    /// model's nodes: a character's motions, [`Importer::character_animations`]).
    ///
    /// # Errors
    /// As [`Importer::model_glb`].
    pub fn model_glb_with(
        &self,
        path: &str,
        animations: &[ffx_phyre::Animation],
    ) -> Result<Vec<u8>> {
        let bytes = self.read(path)?;
        let model =
            ffx_phyre::Model::parse(&bytes).map_err(|e| Error::Model(format!("{path}: {e}")))?;
        let mut pngs: HashMap<String, Option<Vec<u8>>> = HashMap::new();
        let glb = model.to_glb_with(
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
            animations,
        );
        // the morphing instances (map clusters only), the segments' model ids
        let tags = morph::tags(&bytes);
        let model_ids = segment_model_ids(&bytes);
        // a character's cloth segments (the PS2 model and piece the game's
        // cloth binds them to): characters only
        let cloth = if path.contains("/chr/") {
            segment_cloth(&bytes)
        } else {
            Vec::new()
        };
        let morphs = morph::read(&bytes);
        // the segment contexts' stream bindings (which UV set a water
        // shader's TEXCOORD4/6/7 read): only a map cluster has them
        let scene = ffx_scene::map::MapModel::parse(bytes).ok();
        let part_ids = ids::ModelIds::of(path, &model);
        edit_glb(&glb, |doc, bin| {
            self.add_extras(&model, scene.as_ref(), &tags, doc);
            add_morph_targets(&model, &morphs, doc, bin);
            add_color_morph_streams(&model, doc, bin);
            add_model_ids(&model_ids, doc);
            add_cloth(&cloth, doc, bin);
            add_ids(&model, &part_ids, doc);
        })
        .ok_or_else(|| Error::Model(format!("{path}: bad glTF from the export")))
    }

    /// Adds to the export's JSON what gd-ffx draws the model with:
    /// - each material's effect variant states ([`Importer::effect_extras`]:
    ///   `ffx_switches`, `ffx_passes`, `ffx_inputs`), merged into its
    ///   `extras` (glTF material `i` is the model's material `i`);
    /// - each mesh's instance's `m_render_order` (`extras.ffx_render_order`;
    ///   the export writes one mesh per instance with drawable segments, in
    ///   instance order, named as the instance) and its `m_DObjKind` when not
    ///   0 (`extras.ffx_dobj_kind`; 1 sorts first,
    ///   `MorphManager_registerClusterMorphs` `0x006aca10`);
    /// - per material, which of the primitive's `TEXCOORD_n` each of its
    ///   vertex program's `TEXCOORD` inputs reads (`extras.ffx_texcoords`,
    ///   e.g. `{"TEXCOORD0": 0, "TEXCOORD6": 1}`), from the first primitive
    ///   drawn with it ([`texcoord_sources`]); the material's other
    ///   primitives get their `TEXCOORD_n` attributes re-pointed so the same
    ///   numbers hold for them ([`align_texcoords`]); likewise which `COLOR_n`
    ///   each `COLOR` input reads (`extras.ffx_colors`);
    /// - each mesh's instance (`extras.ffx_instance`, its index among the
    ///   cluster's `PMeshInstance`s) and the instance's morph fields
    ///   ([`morph::Tags`]: `ffx_obj_id`, and when not 0 `ffx_anim_ct`,
    ///   `ffx_mime_ct`, `ffx_dobj_flag`);
    /// - [`FORMAT`] as `asset.extras.ffx_import`.
    fn add_extras(
        &self,
        model: &ffx_phyre::Model,
        scene: Option<&ffx_scene::map::MapModel>,
        tags: &[morph::Tags],
        doc: &mut Value,
    ) {
        if let Some(mats) = doc.get_mut("materials").and_then(Value::as_array_mut) {
            for (m, src) in mats.iter_mut().zip(&model.materials) {
                let Some(fx) = src.effect.as_deref().and_then(|e| self.effect_extras(e)) else {
                    continue;
                };
                if !m["extras"].is_object() {
                    m["extras"] = json!({});
                }
                if let (Some(extras), Some(add)) = (m["extras"].as_object_mut(), fx.as_object()) {
                    for (k, v) in add {
                        extras.insert(k.clone(), v.clone());
                    }
                }
            }
        }
        let texcoords = match doc.get_mut("meshes").and_then(Value::as_array_mut) {
            Some(meshes) => self.mesh_extras(model, scene, tags, meshes),
            None => HashMap::new(),
        };
        if let (Some(mats), Some(sc)) = (
            doc.get_mut("materials").and_then(Value::as_array_mut),
            scene,
        ) {
            add_wraps(model, sc, mats);
        }
        if let Some(mats) = doc.get_mut("materials").and_then(Value::as_array_mut) {
            for (i, (t, c)) in texcoords {
                if let Some(m) = mats.get_mut(i).filter(|m| m["extras"].is_object()) {
                    m["extras"]["ffx_texcoords"] = t;
                    m["extras"]["ffx_colors"] = c;
                }
            }
        }
        if !doc["asset"].is_object() {
            doc["asset"] = json!({"version": "2.0"});
        }
        doc["asset"]["extras"] = json!({"ffx_import": FORMAT});
    }

    /// Each mesh's instance, render order, `DObj` kind and morph fields into
    /// its `extras` (see [`Importer::add_extras`]); returns each material's
    /// [`texcoord_sources`] for its `TEXCOORD` and `COLOR` inputs, from the
    /// first primitive drawn with it.
    fn mesh_extras(
        &self,
        model: &ffx_phyre::Model,
        scene: Option<&ffx_scene::map::MapModel>,
        tags: &[morph::Tags],
        meshes: &mut [Value],
    ) -> HashMap<usize, (Value, Value)> {
        let mut texcoords: HashMap<usize, (Value, Value)> = HashMap::new();
        let mut next = 0;
        for mesh in meshes.iter_mut() {
            let Some(name) = mesh.get("name").and_then(Value::as_str).map(str::to_owned) else {
                continue;
            };
            let mesh_segments: Vec<u64> = mesh["extras"]["ffx_segments"]
                .as_array()
                .map(|a| a.iter().filter_map(Value::as_u64).collect())
                .unwrap_or_default();
            // the next instance of that name whose segments hold the mesh's
            let found = model.instances[next.min(model.instances.len())..]
                .iter()
                .position(|inst| {
                    inst.name == name
                        && mesh_segments
                            .iter()
                            .all(|&s| inst.segments.iter().any(|&(x, _)| x as u64 == s))
                })
                .map(|k| next + k);
            let Some(i) = found else {
                continue;
            };
            next = i + 1;
            mesh["extras"]["ffx_instance"] = json!(i);
            if let Some(t) = tags.get(i) {
                mesh["extras"]["ffx_obj_id"] = json!(t.obj_id);
                for (k, v) in [
                    ("ffx_anim_ct", i64::from(t.anim_ct)),
                    ("ffx_mime_ct", i64::from(t.mime_ct)),
                    ("ffx_dobj_flag", i64::from(t.dobj_flag)),
                ] {
                    if v != 0 {
                        mesh["extras"][k] = json!(v);
                    }
                }
            }
            if let Some(b) = model.instances[i].bounds {
                mesh["extras"]["ffx_render_order"] = json!(b.render_order);
            }
            // m_DObjKind 1: the game puts the bounds in the first sort class at map init
            if let Some(si) = scene
                .and_then(|sc| sc.instances.get(i))
                .filter(|si| si.name == model.instances[i].name && si.dobj_kind != 0)
            {
                mesh["extras"]["ffx_dobj_kind"] = json!(si.dobj_kind);
            }
            let Some(prims) = mesh["primitives"].as_array_mut() else {
                continue;
            };
            for p in prims {
                let (Some(seg), Some(mat)) =
                    (p["extras"]["ffx_segment"].as_u64(), p["material"].as_u64())
                else {
                    continue;
                };
                let (Ok(seg), Ok(mat)) = (usize::try_from(seg), usize::try_from(mat)) else {
                    continue;
                };
                let Some(fx) = model
                    .materials
                    .get(mat)
                    .and_then(|m| m.effect.as_deref())
                    .and_then(|e| self.effect_extras(e))
                else {
                    continue;
                };
                let bindings = scene
                    .and_then(|sc| sc.instances.get(i))
                    .filter(|si| si.name == model.instances[i].name)
                    .and_then(|si| {
                        let k = si.segments.iter().position(|&x| x == seg)?;
                        si.contexts.get(k)
                    })
                    .map_or(&[][..], Vec::as_slice);
                let sets = |key: &str| -> Vec<u64> {
                    p["extras"][key]
                        .as_array()
                        .map(|a| a.iter().filter_map(Value::as_u64).collect())
                        .unwrap_or_default()
                };
                let uvs = texcoord_sources(&fx, bindings, &sets("ffx_uv_sets"), "TEXCOORD");
                if let Some((first, _)) = texcoords.get(&mat) {
                    // the material's numbers come from its first primitive: this one's
                    // streams follow them (bsil06's ground shares materials between
                    // instances whose bound UV stream sits at TEXCOORD_1 and TEXCOORD_0)
                    if let Some(attrs) = p["attributes"].as_object_mut() {
                        align_texcoords(attrs, first, &uvs);
                    }
                    continue;
                }
                texcoords.insert(
                    mat,
                    (
                        uvs,
                        texcoord_sources(&fx, bindings, &sets("ffx_color_sets"), "COLOR"),
                    ),
                );
            }
        }
        texcoords
    }

    /// Writes the model at `path` to `out` (made with its folders) unless
    /// `out` is there already; returns `out`.
    ///
    /// # Errors
    /// As [`Importer::model_glb`], and writing.
    pub fn import(&self, path: &str, out: &Path) -> Result<PathBuf> {
        if out.is_file() && glb_format(out) == Some(FORMAT) {
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
    /// When `level` names no map, [`Error::None`] when it is a map of 2D
    /// layers only ([`Importer::map_layers_model`]), or as
    /// [`Importer::import`].
    pub fn import_map(&self, level: &str, cache: &Path) -> Result<PathBuf> {
        let path = map_model_path(level).ok_or_else(|| Error::Missing(level.to_string()))?;
        if !self.has(&path) && self.map_layers_model(level).is_some() {
            return Err(Error::None(format!("{level} has 2D layers only")));
        }
        let name = file_stem(&path);
        self.import(&path, &cache.join("map").join(format!("{name}.glb")))
    }

    /// Map number `map`'s event name (`bsil0200` for map `0x14`): entry
    /// `map` of `event/header/eventid.bin` ([`exits::event_names`]); `None`
    /// past the table or without it.
    #[must_use]
    pub fn event_name(&self, map: u16) -> Option<String> {
        let names = self.events.get_or_init(|| {
            self.list("/event/header/eventid.bin")
                .first()
                .and_then(|p| self.read(p).ok())
                .map(|f| exits::event_names(&f))
                .unwrap_or_default()
        });
        names
            .get(usize::from(map))
            .filter(|n| !n.is_empty())
            .cloned()
    }

    /// The maps (and entrances) map `map`'s event script jumps to with
    /// constant arguments ([`exits::exits`]): where the player most likely
    /// goes next. Empty without the event or its script.
    #[must_use]
    pub fn map_exits(&self, map: u16) -> Vec<(u16, u8)> {
        let Some(name) = self.event_name(map) else {
            return Vec::new();
        };
        let file = format!("/{name}.ebp");
        let Some(path) = self.list(&file).into_iter().find(|p| p.ends_with(&file)) else {
            return Vec::new();
        };
        self.read(&path)
            .ok()
            .and_then(|b| ffx_script::Container::parse(b).ok())
            .map(|c| exits::exits(&c.script))
            .unwrap_or_default()
    }

    /// The archive path of `level`'s map model ([`map_model_path`]) when
    /// the archive has it.
    #[must_use]
    pub fn map_model(&self, level: &str) -> Option<String> {
        map_model_path(level).filter(|p| self.has(p))
    }

    /// The archive path of `level`'s 2D layers when it has them:
    /// `.../map/<area>/<map>/2d/mdl/d3d11/<map>.dae.phyre`, with the
    /// textures in `2d/tex/`. The meshes `scene<s>layer<l>` hold each layer
    /// as a quad in PS2 screen pixels (x, y; 512 x 448) at its depth (z);
    /// ffx-rust's docs/formats/scene-maps.md, "How the game draws the 2D
    /// layers". Maps of 2D layers only (`ptkl14`, `mihn03`, `kami01`...)
    /// have no 3D model; 3D maps may have them too (`bsil00`).
    #[must_use]
    pub fn map_layers_model(&self, level: &str) -> Option<String> {
        let path = map_model_path(level)?.replacen("/mdl/d3d11/", "/2d/mdl/d3d11/", 1);
        self.has(&path).then_some(path)
    }

    /// `level`'s 2D layers ([`Importer::map_layers_model`]) imported under
    /// `cache` (`<cache>/map2d/<map>.glb`).
    ///
    /// # Errors
    /// When `level` names no map, [`Error::None`] when it has no 2D
    /// layers, or as [`Importer::import`].
    pub fn import_map_layers(&self, level: &str, cache: &Path) -> Result<PathBuf> {
        map_model_path(level).ok_or_else(|| Error::Missing(level.to_string()))?;
        let path = self
            .map_layers_model(level)
            .ok_or_else(|| Error::None(format!("{level} has no 2D layers")))?;
        let name = file_stem(&path);
        match self.import(&path, &cache.join("map2d").join(format!("{name}.glb"))) {
            // a 2D model without meshes (`znkd06`)
            Err(Error::Model(e)) if e.contains("no PMeshSegment") => {
                Err(Error::None(format!("{level}'s 2D model has no layers")))
            }
            r => r,
        }
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

/// Which glTF `TEXCOORD_n` of a primitive each `TEXCOORD` input of a
/// material's vertex program reads, as `{"TEXCOORD4": n, ...}` (with
/// `prefix` `COLOR`: which `COLOR_n` each `COLOR` input reads): the
/// game binds an input to the segment's stream of its render type whose
/// `m_streamSet` the instance's segment context names for it
/// (`PMeshInstanceSegmentStreamBinding.m_inputSet`; the water's
/// `TEXCOORD4/6/7`), else to the first such stream (ffx-rust's
/// `ffx_scene::build::vertex_inputs`). The export writes a segment's UV
/// streams as `TEXCOORD_0..` with their sets in `ffx_uv_sets` (`sets`;
/// empty: every set 0).
fn texcoord_sources(
    fx: &Value,
    bindings: &[ffx_scene::map::StreamBinding],
    sets: &[u64],
    prefix: &str,
) -> Value {
    let mut out = serde_json::Map::new();
    let names = fx["ffx_inputs"].as_array().cloned().unwrap_or_default();
    let types = fx["ffx_input_types"]
        .as_array()
        .cloned()
        .unwrap_or_default();
    for (name, ty) in names.iter().zip(&types) {
        let (Some(name), Some(ty)) = (name.as_str(), ty.as_str()) else {
            continue;
        };
        let Some(index) = name.strip_prefix(prefix).and_then(|i| i.parse::<u8>().ok()) else {
            continue;
        };
        let set = bindings
            .iter()
            .find(|b| b.render_type == ty && b.index == index)
            .map(|b| u64::from(b.input_set));
        let k = match set {
            Some(set) if sets.is_empty() => (set == 0).then_some(0),
            Some(set) => sets.iter().position(|&s| s == set),
            None => Some(0),
        };
        if let Some(k) = k {
            out.insert(name.to_owned(), json!(k));
        }
    }
    Value::Object(out)
}

/// Re-points a primitive's `TEXCOORD_n` attributes so that each `TEXCOORD`
/// input reads, at the number its material says (`material`, the
/// material's `ffx_texcoords` from its first primitive), the stream this
/// primitive's own bindings give it (`own`, [`texcoord_sources`] for this
/// primitive): `TEXCOORD_<material[input]>` takes the accessor of
/// `TEXCOORD_<own[input]>`. Accessors are shared, nothing is copied.
fn align_texcoords(attrs: &mut serde_json::Map<String, Value>, material: &Value, own: &Value) {
    let (Some(material), Some(own)) = (material.as_object(), own.as_object()) else {
        return;
    };
    let before = attrs.clone();
    for (input, at) in material {
        let (Some(at), Some(from)) = (at.as_u64(), own.get(input).and_then(Value::as_u64)) else {
            continue;
        };
        if at == from {
            continue;
        }
        if let Some(acc) = before.get(&format!("TEXCOORD_{from}")) {
            attrs.insert(format!("TEXCOORD_{at}"), acc.clone());
        }
    }
    // glTF numbers a primitive's sets from 0 without gaps
    let mut k = 0;
    while !attrs.contains_key(&format!("TEXCOORD_{k}")) {
        let Some(acc) = (k + 1..8).find_map(|j| attrs.get(&format!("TEXCOORD_{j}")).cloned())
        else {
            break;
        };
        attrs.insert(format!("TEXCOORD_{k}"), acc);
        k += 1;
    }
}

/// Each material's sampler address modes ([`sampler_wraps`]) into its
/// `extras.ffx_wrap`.
fn add_wraps(model: &ffx_phyre::Model, sc: &ffx_scene::map::MapModel, mats: &mut [Value]) {
    // the scene reader's material list is not the model's (more entries, another
    // order): the same material by its effect and textures
    let key = |effect: &str, mut tex: Vec<String>| {
        tex.sort();
        (effect.to_ascii_lowercase(), tex)
    };
    let mut by_key: HashMap<(String, Vec<String>), &ffx_scene::map::Material> = HashMap::new();
    for sm in &sc.materials {
        let tex = sm
            .textures
            .iter()
            .map(|t| t.import.to_ascii_lowercase())
            .collect();
        by_key.entry(key(&sm.effect, tex)).or_insert(sm);
    }
    for (m, src) in mats.iter_mut().zip(&model.materials) {
        let tex = src
            .textures
            .iter()
            .map(|t| t.path.to_ascii_lowercase())
            .collect();
        let Some(sm) = by_key.get(&key(src.effect.as_deref().unwrap_or(""), tex)) else {
            continue;
        };
        let wrap = sampler_wraps(src, sm);
        if m["extras"].is_object() && !wrap.is_empty() {
            m["extras"]["ffx_wrap"] = Value::Object(wrap);
        }
    }
}

/// Each texture sampler's address modes (u, v) as the game creates its
/// sampler state (`PSamplerStateD3D11` `0x0058ab10`: 1 wrap, 3 border, 4
/// mirror, else clamp; ffx-rust's `ffx_scene::build::sampler_desc`):
/// `{"TextureSampler": ["WRAP", "CLAMP"], ...}`. A texture and its sampler
/// state share the material's slot offset.
fn sampler_wraps(
    m: &ffx_phyre::Material,
    sm: &ffx_scene::map::Material,
) -> serde_json::Map<String, Value> {
    let name = |w: u8| match w {
        1 => "WRAP",
        3 => "BORDER",
        4 => "MIRROR",
        _ => "CLAMP",
    };
    let mut out = serde_json::Map::new();
    for t in &m.textures {
        let Some(r) = sm
            .textures
            .iter()
            .find(|r| r.import.eq_ignore_ascii_case(&t.path))
        else {
            continue;
        };
        if let Some((_, st)) = sm.samplers.iter().find(|(o, _)| *o == r.offset) {
            out.insert(
                t.sampler.clone(),
                json!([name(st.wrap[0]), name(st.wrap[1])]),
            );
        }
    }
    out
}

/// A glTF binary's JSON chunk: its offset range in `glb`.
fn glb_json_range(glb: &[u8]) -> Option<std::ops::Range<usize>> {
    if glb.get(0..4)? != b"glTF" || glb.get(16..20)? != b"JSON" {
        return None;
    }
    let len = u32::from_le_bytes(glb.get(12..16)?.try_into().ok()?) as usize;
    (20 + len <= glb.len()).then_some(20..20 + len)
}

/// `glb` with its JSON chunk and binary chunk changed by `edit` (which may
/// append to the binary data; `buffers[0].byteLength` follows it).
fn edit_glb(glb: &[u8], edit: impl FnOnce(&mut Value, &mut Vec<u8>)) -> Option<Vec<u8>> {
    let r = glb_json_range(glb)?;
    let mut doc: Value = serde_json::from_slice(&glb[r.clone()]).ok()?;
    // the binary chunk: length, "BIN\0", data
    let mut bin = match glb.get(r.end..r.end + 8) {
        Some(h) if &h[4..8] == b"BIN\0" => {
            let len = u32::from_le_bytes(h[0..4].try_into().ok()?) as usize;
            glb.get(r.end + 8..r.end + 8 + len)?.to_vec()
        }
        _ => Vec::new(),
    };
    let had_bin = !bin.is_empty();
    edit(&mut doc, &mut bin);
    while !bin.len().is_multiple_of(4) {
        bin.push(0);
    }
    if had_bin || !bin.is_empty() {
        doc["buffers"] = json!([{"byteLength": bin.len()}]);
    }
    let mut text = serde_json::to_vec(&doc).ok()?;
    while !text.len().is_multiple_of(4) {
        text.push(b' ');
    }
    let total = 20 + text.len() + if bin.is_empty() { 0 } else { 8 + bin.len() };
    let mut out = Vec::with_capacity(total);
    out.extend(b"glTF");
    out.extend(2u32.to_le_bytes());
    out.extend(u32::try_from(total).ok()?.to_le_bytes());
    out.extend(u32::try_from(text.len()).ok()?.to_le_bytes());
    out.extend(b"JSON");
    out.extend(&text);
    if !bin.is_empty() {
        out.extend(u32::try_from(bin.len()).ok()?.to_le_bytes());
        out.extend(b"BIN\0");
        out.extend(&bin);
    }
    Some(out)
}

/// Appends `bytes` to the binary chunk as a buffer view and an accessor
/// (`accessor`'s fields plus the view); returns the accessor's index.
fn push_accessor(doc: &mut Value, bin: &mut Vec<u8>, bytes: &[u8], mut accessor: Value) -> usize {
    while !bin.len().is_multiple_of(4) {
        bin.push(0);
    }
    if !doc["bufferViews"].is_array() {
        doc["bufferViews"] = json!([]);
    }
    if !doc["accessors"].is_array() {
        doc["accessors"] = json!([]);
    }
    let views = doc["bufferViews"].as_array_mut().expect("an array");
    views.push(
        json!({"buffer": 0, "byteOffset": bin.len(), "byteLength": bytes.len(), "target": 34962}),
    );
    accessor["bufferView"] = json!(views.len() - 1);
    bin.extend_from_slice(bytes);
    let accessors = doc["accessors"].as_array_mut().expect("an array");
    accessors.push(accessor);
    accessors.len() - 1
}

/// A float accessor's bytes, count and per-component bounds.
fn float_accessor<const N: usize>(values: &[[f32; N]]) -> (Vec<u8>, Value) {
    let mut min = [f32::INFINITY; N];
    let mut max = [f32::NEG_INFINITY; N];
    for v in values {
        for k in 0..N {
            min[k] = min[k].min(v[k]);
            max[k] = max[k].max(v[k]);
        }
    }
    let bytes = values
        .iter()
        .flatten()
        .flat_map(|f| f.to_le_bytes())
        .collect();
    let ty = match N {
        2 => "VEC2",
        3 => "VEC3",
        _ => "VEC4",
    };
    (
        bytes,
        json!({"componentType": 5126, "count": values.len(), "type": ty, "min": min.as_slice(), "max": max.as_slice()}),
    )
}

/// The position morphs ([`morph::read`]) as glTF morph targets on the
/// meshes of their instances (`extras.ffx_instance`): the game's vertices
/// are `in0 (1 - w) + in1 w` (docs/importer.md, "Morphing meshes"). Where
/// the stored `Vertex` stream is `in0` (every map seen), one target
/// `in1 - in0` with weight `w`; otherwise two, `in0 - stored` and
/// `in1 - stored`, with weights `1 - w`, `w` (`extras.ffx_morph_targets`
/// 2). A mesh with a segment not covered is left as it is.
#[allow(clippy::many_single_char_names)] // glTF indices
fn add_morph_targets(
    model: &ffx_phyre::Model,
    morphs: &[morph::InstanceMorph],
    doc: &mut Value,
    bin: &mut Vec<u8>,
) {
    let mesh_count = doc["meshes"].as_array().map_or(0, Vec::len);
    for mi in 0..mesh_count {
        let Some(i) = doc["meshes"][mi]["extras"]["ffx_instance"].as_u64() else {
            continue;
        };
        let Some(mo) = morphs.iter().find(|m| m.instance as u64 == i) else {
            continue;
        };
        let Some(inst) = usize::try_from(i).ok().and_then(|i| model.instances.get(i)) else {
            continue;
        };
        // per primitive: its targets (relative positions)
        let prims = doc["meshes"][mi]["primitives"]
            .as_array()
            .cloned()
            .unwrap_or_default();
        let mut per_prim: Vec<Vec<Vec<[f32; 3]>>> = Vec::new();
        for p in &prims {
            let Some(s) = p["extras"]["ffx_segment"]
                .as_u64()
                .and_then(|s| usize::try_from(s).ok())
            else {
                break;
            };
            let Some(k) = inst.segments.iter().position(|&(x, _)| x == s) else {
                break;
            };
            let (Some(sm), Some(seg)) =
                (mo.segments.iter().find(|x| x.k == k), model.segments.get(s))
            else {
                break;
            };
            let (Some(a), Some(b)) = (sm.inputs.first(), sm.inputs.get(1)) else {
                break;
            };
            let n = seg.positions.len();
            if a.width != 3 || b.width != 3 || a.values.len() != n * 3 || b.values.len() != n * 3 {
                break;
            }
            let at = |x: &morph::Input, v: usize| {
                [x.values[3 * v], x.values[3 * v + 1], x.values[3 * v + 2]]
            };
            let same = (0..n).all(|v| {
                let (p, q) = (seg.positions[v], at(a, v));
                (0..3).all(|c| (p[c] - q[c]).abs() <= 1e-6)
            });
            let sub = |x: [f32; 3], y: [f32; 3]| [x[0] - y[0], x[1] - y[1], x[2] - y[2]];
            per_prim.push(if same {
                vec![(0..n).map(|v| sub(at(b, v), at(a, v))).collect()]
            } else {
                vec![
                    (0..n).map(|v| sub(at(a, v), seg.positions[v])).collect(),
                    (0..n).map(|v| sub(at(b, v), seg.positions[v])).collect(),
                ]
            });
        }
        if per_prim.len() != prims.len() || per_prim.is_empty() {
            continue;
        }
        // glTF: every primitive of a mesh has the same number of targets
        let count = per_prim.iter().map(Vec::len).max().unwrap_or(1);
        for (pi, targets) in per_prim.into_iter().enumerate() {
            let targets = if targets.len() == count {
                targets
            } else {
                // in0 is the stored stream here: target 0 is zero, target 1 the delta
                let n = targets[0].len();
                vec![vec![[0.0; 3]; n], targets[0].clone()]
            };
            let mut list = Vec::new();
            for t in &targets {
                let (bytes, acc) = float_accessor(t);
                let a = push_accessor(doc, bin, &bytes, acc);
                list.push(json!({"POSITION": a}));
            }
            doc["meshes"][mi]["primitives"][pi]["targets"] = json!(list);
        }
        let mesh = &mut doc["meshes"][mi];
        if count == 1 {
            mesh["weights"] = json!([0.0]);
            mesh["extras"]["targetNames"] = json!(["morph"]);
        } else {
            mesh["weights"] = json!([1.0, 0.0]);
            mesh["extras"]["targetNames"] = json!(["morph_in0", "morph_in1"]);
        }
        mesh["extras"]["ffx_morph_targets"] = json!(count);
    }
}

/// The stable part IDs ([`ids`]) into the export's JSON: `extras.ffx_id` on
/// glTF node 0 (the model), on each `PNode`'s node (glTF node `1 + n`), on
/// each instance's mesh and its node (the instance, `#<node>/*`) and on each
/// primitive (its part, `#<node>/<k>`: `k` the segment's place in the
/// instance's list); `extras.ffx_texture_ids` on each material (sampler to
/// texture ID); `asset.extras.ffx_model`. Run after [`Importer::add_extras`]
/// (which finds each mesh's instance, `ffx_instance`).
fn add_ids(model: &ffx_phyre::Model, part_ids: &ids::ModelIds, doc: &mut Value) {
    if let Some(mats) = doc.get_mut("materials").and_then(Value::as_array_mut) {
        for (m, src) in mats.iter_mut().zip(&model.materials) {
            if src.textures.is_empty() {
                continue;
            }
            if !m["extras"].is_object() {
                m["extras"] = json!({});
            }
            let texture_ids: serde_json::Map<String, Value> = src
                .textures
                .iter()
                .map(|t| (t.sampler.clone(), json!(ids::texture_id(&t.path))))
                .collect();
            m["extras"]["ffx_texture_ids"] = Value::Object(texture_ids);
        }
    }
    let mut mesh_ids: Vec<Option<String>> = Vec::new();
    if let Some(meshes) = doc.get_mut("meshes").and_then(Value::as_array_mut) {
        for mesh in meshes.iter_mut() {
            // the mesh of a model without instances holds every segment once
            let instance = if model.instances.is_empty() {
                Some(0)
            } else {
                mesh["extras"]["ffx_instance"]
                    .as_u64()
                    .and_then(|i| usize::try_from(i).ok())
            };
            let Some(i) = instance.filter(|&i| i < part_ids.instances.len()) else {
                mesh_ids.push(None);
                continue;
            };
            let id = part_ids.instances[i].clone();
            if !mesh["extras"].is_object() {
                mesh["extras"] = json!({});
            }
            mesh["extras"]["ffx_id"] = json!(id);
            mesh_ids.push(Some(id));
            let segments: Vec<usize> = model
                .instances
                .get(i)
                .map(|inst| inst.segments.iter().map(|&(s, _)| s).collect())
                .unwrap_or_default();
            let mut used = vec![false; segments.len()];
            for p in mesh["primitives"].as_array_mut().into_iter().flatten() {
                let Some(s) = p["extras"]["ffx_segment"]
                    .as_u64()
                    .and_then(|s| usize::try_from(s).ok())
                else {
                    continue;
                };
                let k = if model.instances.is_empty() {
                    Some(s)
                } else {
                    let k = (0..segments.len()).find(|&k| !used[k] && segments[k] == s);
                    if let Some(k) = k {
                        used[k] = true;
                    }
                    k
                };
                if let Some(part) = k.and_then(|k| part_ids.parts[i].get(k)) {
                    p["extras"]["ffx_id"] = json!(part);
                }
            }
        }
    }
    if let Some(nodes) = doc.get_mut("nodes").and_then(Value::as_array_mut) {
        for (j, node) in nodes.iter_mut().enumerate() {
            let id = if j == 0 {
                Some(part_ids.model.clone())
            } else if let Some(m) = node.get("mesh").and_then(Value::as_u64) {
                usize::try_from(m)
                    .ok()
                    .and_then(|m| mesh_ids.get(m).cloned().flatten())
            } else {
                part_ids.nodes.get(j - 1).cloned()
            };
            if let Some(id) = id {
                if !node["extras"].is_object() {
                    node["extras"] = json!({});
                }
                node["extras"]["ffx_id"] = json!(id);
            }
        }
    }
    if doc["asset"]["extras"].is_object() {
        doc["asset"]["extras"]["ffx_model"] = json!(part_ids.model);
    }
}

/// Each `PMeshSegment`'s model id (`+0x18`, a word; the segments are
/// `0x6c` bytes): the model a character's unskinned mesh belongs to,
/// which `graphicCharacterInstanceSetWorldMatrixByMdlID` (`0x0063c430`)
/// places at the model's base element (ffx-rust's `ffx-graphic`
/// `character_set_world_matrix_by_mdl_id`). Empty for other layouts.
fn segment_model_ids(bytes: &[u8]) -> Vec<u32> {
    let Ok(c) = ffx_phyre::cluster::Cluster::parse(bytes) else {
        return Vec::new();
    };
    let Some(l) = c.list("PMeshSegment").filter(|l| l.object_size() == 0x6c) else {
        return Vec::new();
    };
    (0..l.count)
        .map_while(|i| {
            let o = l.object(i) + 0x18;
            Some(u32::from_le_bytes(bytes.get(o..o + 4)?.try_into().ok()?))
        })
        .collect()
}

/// Each primitive's segment's model id ([`segment_model_ids`]) as
/// `extras.ffx_model_id`, and each mesh's (its first primitive's) as the
/// mesh's `extras.ffx_model_id`.
fn add_model_ids(ids: &[u32], doc: &mut Value) {
    let Some(meshes) = doc["meshes"].as_array_mut() else {
        return;
    };
    for mesh in meshes {
        let mut first = None;
        if let Some(prims) = mesh["primitives"].as_array_mut() {
            for p in prims {
                let id = p["extras"]["ffx_segment"]
                    .as_u64()
                    .and_then(|s| usize::try_from(s).ok())
                    .and_then(|s| ids.get(s));
                if let Some(&id) = id {
                    p["extras"]["ffx_model_id"] = json!(id);
                    first.get_or_insert(id);
                }
            }
        }
        if let Some(id) = first {
            mesh["extras"]["ffx_model_id"] = json!(id);
        }
    }
}

/// Each `PMeshSegment`'s PS2 model and piece (`+0x20`, `+0x24`, the low
/// words, as ffx-rust's `ffx_host::cloth` reads them): a segment the game's
/// cloth moves has both other than `0xffff` (`Sg_ClothPlaceAnchors` binds
/// its vertices to PS2 sub-mesh `piece % 100` of that model; ffx-rust's
/// docs/state-api.md, "Cloth"). `None` for the others; empty for other
/// layouts.
fn segment_cloth(bytes: &[u8]) -> Vec<Option<(u16, u16)>> {
    let Ok(c) = ffx_phyre::cluster::Cluster::parse(bytes) else {
        return Vec::new();
    };
    let Some(l) = c.list("PMeshSegment").filter(|l| l.object_size() == 0x6c) else {
        return Vec::new();
    };
    let word = |at: usize| Some(u16::from_le_bytes(bytes.get(at..at + 2)?.try_into().ok()?));
    (0..l.count)
        .map(|i| {
            let seg = l.object(i);
            match (word(seg + 0x20), word(seg + 0x24)) {
                (Some(model), Some(piece)) if model != 0xffff && piece != 0xffff => {
                    Some((model, piece))
                }
                _ => None,
            }
        })
        .collect()
}

/// The cloth segments ([`segment_cloth`]) of each mesh (one per instance):
/// each such primitive gets `extras.ffx_cloth` = `[model, piece, first,
/// count, texcoord]` and a vertex number attribute: the mesh's cloth
/// vertices numbered in primitive order (`first` the primitive's first,
/// `count` its vertices, its vertex `k` numbered `first + k`), written as
/// `TEXCOORD_<texcoord>` (`[first + k, 0]`), the first `TEXCOORD_n` from 2
/// the primitive does not have (Godot's glTF loader reads `TEXCOORD_2` and
/// up as its `CUSTOM0..3` channels, two to a channel, in order). The
/// frontend finds the game's cloth vertex `k` (`ffx_host::cloth`, in the
/// segment's vertex order, which the export keeps) by that number whatever
/// order and sharing Godot's loader gives the vertices. The mesh gets
/// `extras.ffx_cloth_vertices`, its cloth vertices in all.
fn add_cloth(cloth: &[Option<(u16, u16)>], doc: &mut Value, bin: &mut Vec<u8>) {
    if cloth.iter().all(Option::is_none) {
        return;
    }
    let mesh_count = doc["meshes"].as_array().map_or(0, Vec::len);
    for mi in 0..mesh_count {
        let prim_count = doc["meshes"][mi]["primitives"]
            .as_array()
            .map_or(0, Vec::len);
        let mut first: u64 = 0;
        for pi in 0..prim_count {
            let p = &doc["meshes"][mi]["primitives"][pi];
            let Some(Some((model, piece))) = p["extras"]["ffx_segment"]
                .as_u64()
                .and_then(|s| usize::try_from(s).ok())
                .and_then(|s| cloth.get(s))
                .copied()
            else {
                continue;
            };
            let Some(count) = p["attributes"]["POSITION"]
                .as_u64()
                .and_then(|a| doc["accessors"][usize::try_from(a).ok()?]["count"].as_u64())
            else {
                continue;
            };
            let Some(texcoord) =
                (2..8).find(|n| p["attributes"].get(format!("TEXCOORD_{n}")).is_none())
            else {
                continue;
            };
            #[allow(clippy::cast_precision_loss)] // vertex numbers below 2^24
            let numbers: Vec<[f32; 2]> = (first..first + count).map(|k| [k as f32, 0.0]).collect();
            let (bytes, acc) = float_accessor(&numbers);
            let a = push_accessor(doc, bin, &bytes, acc);
            let p = &mut doc["meshes"][mi]["primitives"][pi];
            p["attributes"][format!("TEXCOORD_{texcoord}")] = json!(a);
            p["extras"]["ffx_cloth"] = json!([model, piece, first, count, texcoord]);
            first += count;
        }
        if first > 0 {
            doc["meshes"][mi]["extras"]["ffx_cloth_vertices"] = json!(first);
        }
    }
}

/// For materials of a vertex colour morph (`VERTEX_COLOR_MORPH`: the vertex
/// program's colour is `COLOR0 + w (COLOR1 - COLOR0)`), the `COLOR_n` that
/// `COLOR1` reads (the material's `ffx_colors`) as two float pairs,
/// `TEXCOORD_2` (r, g) and `TEXCOORD_3` (b, a): Godot imports no `COLOR_1`
/// but reads those as its `CUSTOM0`.
#[allow(clippy::many_single_char_names)] // glTF indices
fn add_color_morph_streams(model: &ffx_phyre::Model, doc: &mut Value, bin: &mut Vec<u8>) {
    let Some(mats) = doc["materials"].as_array() else {
        return;
    };
    let second: Vec<Option<u64>> = mats
        .iter()
        .map(|m| {
            let x = &m["extras"];
            x["ffx_switches"]
                .get("VERTEX_COLOR_MORPH")
                .map(|_| x["ffx_colors"]["COLOR1"].as_u64().unwrap_or(1))
        })
        .collect();
    let mesh_count = doc["meshes"].as_array().map_or(0, Vec::len);
    for mi in 0..mesh_count {
        let prim_count = doc["meshes"][mi]["primitives"]
            .as_array()
            .map_or(0, Vec::len);
        for pi in 0..prim_count {
            let p = &doc["meshes"][mi]["primitives"][pi];
            let (Some(mat), Some(s)) =
                (p["material"].as_u64(), p["extras"]["ffx_segment"].as_u64())
            else {
                continue;
            };
            let Some(Some(k)) = usize::try_from(mat).ok().and_then(|m| second.get(m)) else {
                continue;
            };
            if p["attributes"].get("TEXCOORD_2").is_some() {
                continue;
            }
            let Some(seg) = usize::try_from(s).ok().and_then(|s| model.segments.get(s)) else {
                continue;
            };
            let colors = match *k {
                0 => &seg.colors,
                k => match usize::try_from(k - 1)
                    .ok()
                    .and_then(|k| seg.extra_colors.get(k))
                {
                    Some(c) => c,
                    None => continue,
                },
            };
            if colors.len() != seg.positions.len() || colors.is_empty() {
                continue;
            }
            let f = |c: u8| f32::from(c) / 255.0;
            let rg: Vec<[f32; 2]> = colors.iter().map(|c| [f(c[0]), f(c[1])]).collect();
            let ba: Vec<[f32; 2]> = colors.iter().map(|c| [f(c[2]), f(c[3])]).collect();
            let (bytes, acc) = float_accessor(&rg);
            let a = push_accessor(doc, bin, &bytes, acc);
            let (bytes, acc) = float_accessor(&ba);
            let b = push_accessor(doc, bin, &bytes, acc);
            let p = &mut doc["meshes"][mi]["primitives"][pi];
            p["attributes"]["TEXCOORD_2"] = json!(a);
            p["attributes"]["TEXCOORD_3"] = json!(b);
        }
    }
}

/// The importer format (`asset.extras.ffx_import`) of the `.glb` at
/// `path`; `None` for an older import or an unreadable file.
pub(crate) fn glb_format(path: &Path) -> Option<u64> {
    use std::io::Read;
    let mut f = std::fs::File::open(path).ok()?;
    let mut head = [0u8; 20];
    f.read_exact(&mut head).ok()?;
    if &head[0..4] != b"glTF" || &head[16..20] != b"JSON" {
        return None;
    }
    let len = u32::from_le_bytes(head[12..16].try_into().ok()?) as usize;
    let mut text = vec![0u8; len];
    f.read_exact(&mut text).ok()?;
    let doc: Value = serde_json::from_slice(&text).ok()?;
    doc["asset"]["extras"]["ffx_import"].as_u64()
}

/// `.../<name>.dae.phyre`'s name.
fn file_stem(path: &str) -> String {
    let file = path.rsplit('/').next().unwrap_or(path);
    file.strip_suffix(".dae.phyre").unwrap_or(file).to_string()
}

/// A texture face's pixels ([`Importer::texture_rgba`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Pixels {
    pub width: u32,
    pub height: u32,
    /// The texture's faces (1, or 6 for a cube map).
    pub faces: usize,
    /// RGBA8 rows, top row first.
    pub rgba: Vec<u8>,
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

    fn glb_json(glb: &[u8]) -> Value {
        serde_json::from_slice(&glb[glb_json_range(glb).unwrap()]).unwrap()
    }

    #[test]
    fn texcoord_inputs_follow_the_stream_bindings() {
        let fx = json!({
            "ffx_inputs": ["POSITION0", "TEXCOORD0", "TEXCOORD6", "TEXCOORD7"],
            "ffx_input_types": ["Vertex", "ST", "ST", "ST"],
        });
        let bind = |index, input_set| ffx_scene::map::StreamBinding {
            render_type: "ST".into(),
            name: String::new(),
            index,
            input_set,
        };
        // TEXCOORD7 bound to stream set 1, the primitive's second UV set; the others unbound
        let t = texcoord_sources(&fx, &[bind(7, 1)], &[0, 1], "TEXCOORD");
        assert_eq!(t, json!({"TEXCOORD0": 0, "TEXCOORD6": 0, "TEXCOORD7": 1}));
        // a set the primitive does not have: left out
        let t = texcoord_sources(&fx, &[bind(6, 2)], &[0, 1], "TEXCOORD");
        assert_eq!(t, json!({"TEXCOORD0": 0, "TEXCOORD7": 0}));
    }

    #[test]
    fn texcoords_follow_the_material_on_its_other_primitives() {
        // the material's first primitive reads TEXCOORD0 at TEXCOORD_1; this one has
        // its bound stream at TEXCOORD_0 (one UV stream, set 1)
        let mut attrs = json!({"POSITION": 0, "TEXCOORD_0": 5})
            .as_object()
            .unwrap()
            .clone();
        align_texcoords(
            &mut attrs,
            &json!({"TEXCOORD0": 1}),
            &json!({"TEXCOORD0": 0}),
        );
        assert_eq!(
            Value::Object(attrs),
            json!({"POSITION": 0, "TEXCOORD_0": 5, "TEXCOORD_1": 5})
        );
        // the other way: the material reads TEXCOORD_0, this primitive's stream is its second
        let mut attrs = json!({"TEXCOORD_0": 5, "TEXCOORD_1": 6})
            .as_object()
            .unwrap()
            .clone();
        align_texcoords(
            &mut attrs,
            &json!({"TEXCOORD0": 0}),
            &json!({"TEXCOORD0": 1}),
        );
        assert_eq!(
            Value::Object(attrs),
            json!({"TEXCOORD_0": 6, "TEXCOORD_1": 6})
        );
        // the same numbers: nothing changes
        let mut attrs = json!({"TEXCOORD_0": 5, "TEXCOORD_1": 6})
            .as_object()
            .unwrap()
            .clone();
        align_texcoords(
            &mut attrs,
            &json!({"TEXCOORD0": 0, "TEXCOORD7": 1}),
            &json!({"TEXCOORD0": 0, "TEXCOORD7": 1}),
        );
        assert_eq!(
            Value::Object(attrs),
            json!({"TEXCOORD_0": 5, "TEXCOORD_1": 6})
        );
    }

    #[test]
    fn glb_json_edits_keep_the_binary_chunk() {
        let mut glb = b"glTF".to_vec();
        let text = br#"{"asset":{"version":"2.0"}}  "#;
        let bin = [1u8, 2, 3, 4];
        let total = 20 + text.len() + 8 + bin.len();
        glb.extend(2u32.to_le_bytes());
        glb.extend(u32::try_from(total).unwrap().to_le_bytes());
        glb.extend(u32::try_from(text.len()).unwrap().to_le_bytes());
        glb.extend(b"JSON");
        glb.extend(text);
        glb.extend(4u32.to_le_bytes());
        glb.extend(b"BIN\0");
        glb.extend(bin);
        let out = edit_glb(&glb, |d, _| {
            d["asset"]["extras"] = json!({"ffx_import": FORMAT});
        })
        .unwrap();
        assert_eq!(glb_json(&out)["asset"]["extras"]["ffx_import"], FORMAT);
        assert_eq!(&out[out.len() - 12..], &glb[glb.len() - 12..]);
        let declared = u32::from_le_bytes(out[8..12].try_into().unwrap()) as usize;
        assert_eq!(declared, out.len());
        // appended data: a view after the old bytes, the buffer's length follows
        let out = edit_glb(&glb, |d, b| {
            push_accessor(d, b, &[5, 6, 7, 8, 9, 10, 11, 12], json!({"count": 2}));
        })
        .unwrap();
        let doc = glb_json(&out);
        assert_eq!(doc["bufferViews"][0]["byteOffset"], 4);
        assert_eq!(doc["accessors"][0]["bufferView"], 0);
        assert_eq!(doc["buffers"][0]["byteLength"], 12);
        assert_eq!(
            &out[out.len() - 12..],
            &[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]
        );
    }

    /// The cloth segments' primitives get their PS2 model and piece, their
    /// first vertex number among the mesh's cloth vertices and a vertex
    /// number attribute on the first free `TEXCOORD_n` from 2; the others
    /// are left as they are.
    #[test]
    fn numbers_the_cloth_vertices() {
        let mut doc = json!({
            "accessors": [{"count": 3}, {"count": 2}, {"count": 4}],
            "meshes": [{"primitives": [
                {"attributes": {"POSITION": 0, "TEXCOORD_0": 0}, "extras": {"ffx_segment": 0}},
                {"attributes": {"POSITION": 1}, "extras": {"ffx_segment": 1}},
                {"attributes": {"POSITION": 2, "TEXCOORD_2": 0}, "extras": {"ffx_segment": 2}},
            ]}],
        });
        let mut bin = Vec::new();
        add_cloth(&[Some((0, 1)), None, Some((1, 102))], &mut doc, &mut bin);
        let prims = &doc["meshes"][0]["primitives"];
        assert_eq!(prims[0]["extras"]["ffx_cloth"], json!([0, 1, 0, 3, 2]));
        assert!(prims[1]["extras"].get("ffx_cloth").is_none());
        assert!(prims[1]["attributes"].get("TEXCOORD_2").is_none());
        assert_eq!(prims[2]["extras"]["ffx_cloth"], json!([1, 102, 3, 4, 3]));
        assert_eq!(doc["meshes"][0]["extras"]["ffx_cloth_vertices"], 7);
        // the second cloth primitive's numbers: 3..7 as (k, 0) floats
        let acc = &doc["accessors"]
            [usize::try_from(prims[2]["attributes"]["TEXCOORD_3"].as_u64().unwrap()).unwrap()];
        assert_eq!(acc["count"], 4);
        assert_eq!(acc["type"], "VEC2");
        let view =
            &doc["bufferViews"][usize::try_from(acc["bufferView"].as_u64().unwrap()).unwrap()];
        let at = usize::try_from(view["byteOffset"].as_u64().unwrap()).unwrap();
        let f = |i: usize| f32::from_le_bytes(bin[at + 4 * i..at + 4 * i + 4].try_into().unwrap());
        assert_eq!([f(0), f(1), f(2), f(6)], [3.0, 0.0, 4.0, 6.0]);
    }

    /// On the player's game (skips without `FFX_GAME_DIR`): the beach's
    /// event (map `0x46`, `bsil0000`) walks off to map `0x14` (`bsil0200`,
    /// `Native_MapChange(20)` on its exit line; ffx-rust's docs/harness.md).
    #[test]
    fn the_beach_exits_to_the_next_map() {
        let Some(dir) = std::env::var_os("FFX_GAME_DIR") else {
            eprintln!("FFX_GAME_DIR is not set; skipping");
            return;
        };
        let imp = Importer::open(Path::new(&dir)).unwrap();
        assert_eq!(imp.event_name(0x46).as_deref(), Some("bsil0000"));
        assert_eq!(imp.event_name(0x14).as_deref(), Some("bsil0200"));
        let exits = imp.map_exits(0x46);
        assert!(exits.iter().any(|&(m, _)| m == 0x14), "{exits:x?}");
    }

    /// On the player's game (skips without `FFX_GAME_DIR`): Besaid's birds
    /// (objects 18..32, display objects the scripts fly) are position morphs
    /// whose base is the stored mesh; their mesh gets one morph target and
    /// the instance's fields.
    #[test]
    fn exports_the_morphing_meshes() {
        let Some(dir) = std::env::var_os("FFX_GAME_DIR") else {
            eprintln!("FFX_GAME_DIR is not set; skipping");
            return;
        };
        let imp = Importer::open(Path::new(&dir)).unwrap();
        let path = imp.map_model("/map/bsil/bsil00/").unwrap();
        let bytes = imp.read(&path).unwrap();
        let morphs = morph::read(&bytes);
        assert_eq!(morphs.len(), 22);
        assert!(morphs.iter().all(|m| m.modifier == "Morph23"));
        let tags = morph::tags(&bytes);
        assert_eq!(tags[7].obj_id, 18);
        assert_eq!(tags[7].mime_ct, 1);
        let doc = glb_json(&imp.model_glb(&path).unwrap());
        let bird = doc["meshes"]
            .as_array()
            .unwrap()
            .iter()
            .find(|m| m["name"] == "FBXASC0498_0Shape")
            .unwrap();
        assert_eq!(bird["extras"]["ffx_obj_id"], 18);
        assert_eq!(bird["extras"]["ffx_mime_ct"], 1);
        assert_eq!(bird["extras"]["ffx_morph_targets"], 1);
        assert_eq!(bird["weights"], json!([0.0]));
        let target = &bird["primitives"][0]["targets"][0]["POSITION"];
        let acc = &doc["accessors"][usize::try_from(target.as_u64().unwrap()).unwrap()];
        assert_eq!(acc["count"], 96);
        // Tidus's cloth (ffx-rust's docs/state-api.md, "Cloth": model 0
        // pieces 0, 1, 2 and model 1 pieces 1, 2; 68, 28, 22, 16 and 64
        // vertices), all in his one mesh
        let path = character_model_path("c001").unwrap();
        let doc = glb_json(&imp.model_glb(&path).unwrap());
        let mut cloth: Vec<(u64, u64, u64)> = doc["meshes"][0]["primitives"]
            .as_array()
            .unwrap()
            .iter()
            .filter_map(|p| {
                let c = p["extras"]["ffx_cloth"].as_array()?;
                Some((c[0].as_u64()?, c[1].as_u64()?, c[3].as_u64()?))
            })
            .collect();
        cloth.sort_unstable();
        assert_eq!(
            cloth,
            [(0, 0, 68), (0, 1, 28), (0, 2, 22), (1, 1, 16), (1, 2, 64)]
        );
        assert_eq!(doc["meshes"][0]["extras"]["ffx_cloth_vertices"], 198);
        // the save sphere's three unskinned parts: models 0, 1, 2 (MeshNode0..2)
        let path = character_model_path("f001").unwrap();
        let doc = glb_json(&imp.model_glb(&path).unwrap());
        let ids: Vec<(String, Value)> = doc["meshes"]
            .as_array()
            .unwrap()
            .iter()
            .map(|m| {
                (
                    m["name"].as_str().unwrap().to_owned(),
                    m["extras"]["ffx_model_id"].clone(),
                )
            })
            .collect();
        assert_eq!(
            ids,
            [
                ("MeshNode0Shape".to_owned(), json!(0)),
                ("MeshNode1Shape".to_owned(), json!(1)),
                ("MeshNode2Shape".to_owned(), json!(2))
            ]
        );
    }

    /// On the player's game (skips without `FFX_GAME_DIR`): Besaid beach's
    /// stable part IDs on the glTF nodes, meshes, primitives and materials,
    /// the same on a second export.
    #[test]
    fn exports_stable_part_ids() {
        let Some(dir) = std::env::var_os("FFX_GAME_DIR") else {
            eprintln!("FFX_GAME_DIR is not set; skipping");
            return;
        };
        let imp = Importer::open(Path::new(&dir)).unwrap();
        let path = imp.map_model("/map/bsil/bsil00/").unwrap();
        let model = "x:/ffx_data/gamedata/ps3data/map/bsil/bsil00/mdl/d3d11/bsil00.dae.phyre";
        let doc = glb_json(&imp.model_glb(&path).unwrap());
        assert_eq!(doc["asset"]["extras"]["ffx_model"], model);
        assert_eq!(doc["nodes"][0]["extras"]["ffx_id"], model);
        // a bird (object 18): an instance without a node of its own
        let bird = doc["meshes"]
            .as_array()
            .unwrap()
            .iter()
            .find(|m| m["name"] == "FBXASC0498_0Shape")
            .unwrap();
        assert_eq!(
            bird["extras"]["ffx_id"],
            format!("{model}#FBXASC0498_0Shape/*")
        );
        assert_eq!(
            bird["primitives"][0]["extras"]["ffx_id"],
            format!("{model}#FBXASC0498_0Shape/0")
        );
        // every mesh, its node and every primitive has an ID; parts are unique
        let mut parts = std::collections::HashSet::new();
        for (m, mesh) in doc["meshes"].as_array().unwrap().iter().enumerate() {
            let id = mesh["extras"]["ffx_id"].as_str().unwrap();
            assert!(
                id.starts_with(&format!("{model}#")) && id.ends_with("/*"),
                "{id}"
            );
            let node = doc["nodes"]
                .as_array()
                .unwrap()
                .iter()
                .find(|n| n["mesh"] == m)
                .unwrap();
            assert_eq!(node["extras"]["ffx_id"], id);
            for p in mesh["primitives"].as_array().unwrap() {
                let part = p["extras"]["ffx_id"].as_str().unwrap();
                assert_eq!(ids::instance_of(part).as_deref(), Some(id));
                assert!(parts.insert(part.to_owned()), "{part} twice");
            }
        }
        assert!(parts.len() > 100, "{} parts", parts.len());
        // the textures by their archive path
        let tex = doc["materials"]
            .as_array()
            .unwrap()
            .iter()
            .find_map(|m| m["extras"]["ffx_texture_ids"]["TextureSampler"].as_str())
            .unwrap();
        assert!(
            tex.starts_with("x:/ffx_data/gamedata/ps3data/map/bsil/bsil00/tex/d3d11/")
                && tex.ends_with(".dds.phyre"),
            "{tex}"
        );
        assert!(imp.has(&tex[3..]), "{tex} is in the archive");
        // a second export: the same IDs
        let again = glb_json(&imp.model_glb(&path).unwrap());
        let ids_of = |d: &Value| -> Vec<Value> {
            d["meshes"]
                .as_array()
                .unwrap()
                .iter()
                .flat_map(|m| {
                    m["primitives"]
                        .as_array()
                        .unwrap()
                        .iter()
                        .map(|p| p["extras"]["ffx_id"].clone())
                })
                .collect()
        };
        assert_eq!(ids_of(&doc), ids_of(&again));
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
        // gear names (MsWeaponName): w_name record 0 is each character's
        // strongest weapon; string k is character k's
        let w = |name, chr| cs.decode(&imp.weapon_name("us", name, chr, false).unwrap());
        assert_eq!(w(0, 0), "Caladbolg");
        assert_eq!(w(0, 6), "Godhand");
        assert_eq!(w(1, 0), "Brotherhood");
        assert_eq!(w(0x1000, 0), "Caladbolg", "the low 12 bits");
        assert_eq!(w(0xfff, 1), w(0, 1), "a record past the table: the first");
        // the localisation kit: the config's PC row (title 0x1cd, options
        // 0x1d0 - 2 k)
        let loc = |i| cs.decode(&imp.loc_text("us", i).unwrap());
        assert_eq!(loc(0x1cd), "Music");
        assert_eq!(loc(0x1d0), "Arranged");
        assert_eq!(loc(0x1ce), "Original");
        let map = imp.import_map("/map/znkd/znkd06/", &cache).unwrap();
        // a map of 2D layers only, a 2D model without meshes
        assert!(matches!(
            imp.import_map("/map/kami/kami01/", &cache),
            Err(Error::None(_))
        ));
        let layers = imp.import_map_layers("/map/kami/kami01/", &cache).unwrap();
        // kami01's lamp glows (layers 7..10) add and write no depth; its pictures do neither
        let names = ["MASK_DEPTH", "ADDITIVE_BLENDING"];
        let fx = "PS3Data/Shaders/PhyrePrerenderedShader.fx#";
        assert_eq!(
            imp.effect_switches(&format!("{fx}76FCBCF8ED1839AC14A6288F177585D7"), &names),
            names
        );
        assert_eq!(
            imp.effect_switches(&format!("{fx}987968ADFCEDE47EA4871B4290C3ED1B"), &names),
            Vec::<&str>::new()
        );
        assert!(layers.ends_with("map2d/kami01.glb"), "{}", layers.display());
        assert!(matches!(
            imp.import_map_layers("/map/znkd/znkd06/", &cache),
            Err(Error::None(_))
        ));
        // the map's materials carry their effect variant's states, its meshes their render order
        let doc = glb_json(&std::fs::read(&map).unwrap());
        assert_eq!(doc["asset"]["extras"]["ffx_import"], FORMAT);
        let mats = doc["materials"].as_array().unwrap();
        assert!(mats.iter().all(|m| m["extras"]["ffx_passes"].is_array()));
        assert!(
            mats.iter()
                .any(|m| m["extras"]["ffx_wrap"]["TextureSampler"].is_array())
        );
        let meshes = doc["meshes"].as_array().unwrap();
        assert!(
            meshes
                .iter()
                .all(|m| m["extras"]["ffx_render_order"].is_i64())
        );
        // bsil00: blended scenery (src alpha over the frame, writing depth), the beach sea's
        // refraction water (postTransparent, no depth write) reading UV sets 0 and 1
        let besaid =
            glb_json(&std::fs::read(imp.import_map("/map/bsil/bsil00/", &cache).unwrap()).unwrap());
        let mats = besaid["materials"].as_array().unwrap();
        let blended = mats
            .iter()
            .find(|m| {
                m["extras"]["ffx_switches"]["ALPHA_ENABLED"].is_string()
                    && m["extras"]["ffx_effect"]
                        .as_str()
                        .unwrap()
                        .contains("DefaultLit")
            })
            .unwrap();
        let pass = &blended["extras"]["ffx_passes"][0];
        assert_eq!(pass["blend"]["src"], "SRC_ALPHA");
        assert_eq!(pass["blend"]["dst"], "INV_SRC_ALPHA");
        assert_eq!(pass["depth_write"], true);
        let sea = mats
            .iter()
            .find(|m| m["extras"]["ffx_switches"]["REFRACTION_ENABLED"].is_string())
            .unwrap();
        let post = sea["extras"]["ffx_passes"]
            .as_array()
            .unwrap()
            .iter()
            .find(|p| p["pass"] == "postTransparent")
            .unwrap();
        assert_eq!(post["depth_write"], false);
        assert_eq!(sea["extras"]["ffx_texcoords"]["TEXCOORD6"], 0);
        assert_eq!(sea["extras"]["ffx_texcoords"]["TEXCOORD7"], 1);
        let chr = imp.import_character("c001", &cache).unwrap();
        for p in [&map, &chr, &layers] {
            let b = std::fs::read(p).unwrap();
            assert_eq!(&b[0..4], b"glTF", "{}", p.display());
            assert!(b.len() > 10_000, "{}: {} bytes", p.display(), b.len());
        }
        std::fs::remove_dir_all(&cache).unwrap();
    }
}

/// Line `index` of a localisation kit's bytes, as
/// `LocalizationManager_getText` takes it: lines end at `\n`, `\r` is
/// dropped, an index past the last line gives the last one (the text after
/// the last `\n`, which may be empty).
#[must_use]
pub fn loc_line(kit: &[u8], index: u32) -> Vec<u8> {
    let mut line = Vec::new();
    let mut seen = 0u32;
    for &c in kit {
        match c {
            b'\n' => {
                if seen == index {
                    break;
                }
                seen += 1;
                line.clear();
            }
            b'\r' => {}
            _ => line.push(c),
        }
    }
    // the original copies up to the first NUL
    if let Some(end) = line.iter().position(|&c| c == 0) {
        line.truncate(end);
    }
    line
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
    use super::{loc_line, place_name_in};

    /// The localisation kit's lines: `\r` dropped, past the end the last.
    #[test]
    fn loc_lines_are_counted_by_newlines() {
        let kit = b"zero\r\none\n\nthree";
        assert_eq!(loc_line(kit, 0), b"zero");
        assert_eq!(loc_line(kit, 1), b"one");
        assert_eq!(loc_line(kit, 2), b"");
        assert_eq!(loc_line(kit, 3), b"three");
        assert_eq!(loc_line(kit, 9), b"three", "past the end: the last line");
        assert_eq!(
            loc_line(b"a\n", 5),
            b"",
            "a kit ending in a newline: an empty last line"
        );
    }

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
