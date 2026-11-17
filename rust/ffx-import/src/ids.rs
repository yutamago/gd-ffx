//! Stable part IDs (docs/importer.md, "Stable part IDs"): every classic
//! model, node, mesh instance, segment and texture gets an ID made from the
//! archive's own names, so it is the same on every import and on every
//! player's copy. The forms are ffx-remix's (its docs/scene-interface.md,
//! "Stable IDs"; docs/pack-format.md, "scene"):
//!
//! | What | ID |
//! |---|---|
//! | a model | `x:/<archive path>`, e.g. `x:/ffx_data/gamedata/ps3data/map/bsil/bsil00/mdl/d3d11/bsil00.dae.phyre` |
//! | a node (`PNode`) | `<model>#<node>` |
//! | a mesh instance (`PMeshInstance`, every segment) | `<model>#<node>/*` |
//! | a part (one segment of an instance) | `<model>#<node>/<k>` |
//! | a texture | `x:/<archive path>`, e.g. `x:/ffx_data/gamedata/ps3data/map/bsil/bsil00/tex/d3d11/sand.dds.phyre` |
//! | a field map (an event) | `x:map/<event>`, e.g. `x:map/bsil0000` |
//! | a battle stage | `x:map/<stage>`, e.g. `x:map/bika00_a` |
//! | a level (gd-ffx's map key) | `x:level/<level>`, e.g. `x:level/bsil00` |
//!
//! An instance's `<node>` is the name of the `PNode` that shares its world
//! matrix, or the instance's own name when none does; two instances on one
//! name (`nagi00`) get `~1`, `~2`... in instance order. Nodes on one name
//! likewise. `<k>` is the segment's place in the instance's segment list.
//! Archive paths are lower case. A model without instances (every segment
//! shown once) has the instance `#mesh/*` and the parts `#mesh/<segment>`.

use std::collections::HashMap;

/// A model's ID: `x:/` and its archive path, lower case.
#[must_use]
pub fn model_id(archive_path: &str) -> String {
    format!(
        "x:/{}",
        archive_path
            .trim_start_matches('/')
            .replace('\\', "/")
            .to_ascii_lowercase()
    )
}

/// A material texture's ID from the path the model stores
/// (`PS3Data/map/bsil/bsil00/tex/sand.dds`): `x:/` and the texture's archive
/// path (`ffx_data/gamedata/ps3data/map/bsil/bsil00/tex/d3d11/sand.dds.phyre`).
#[must_use]
pub fn texture_id(stored_path: &str) -> String {
    model_id(&ffx_scene::map::archive_path(stored_path))
}

/// A field map's or battle stage's ID (`x:map/bsil0000`, `x:map/bika00_a`).
#[must_use]
pub fn map_id(name: &str) -> String {
    format!("x:map/{}", name.to_ascii_lowercase())
}

/// A level's ID as gd-ffx keys maps (`x:level/bsil00`): the last part of
/// the game's level name (`/map/bsil/bsil00/`, `/btlmap/bika/bika00_a/`).
#[must_use]
pub fn level_id(level: &str) -> String {
    format!("x:level/{}", level_key(level))
}

/// gd-ffx's key of a level: the last part of its name, lower case
/// (`/map/bsil/bsil00/` and `bsil00` are `bsil00`).
#[must_use]
pub fn level_key(level: &str) -> String {
    level
        .trim()
        .trim_matches('/')
        .rsplit('/')
        .next()
        .unwrap_or_default()
        .to_ascii_lowercase()
}

/// The instance ID (`...#<node>/*`) of a part ID (`...#<node>/<k>`); an
/// instance ID as it is; `None` for anything else.
#[must_use]
pub fn instance_of(id: &str) -> Option<String> {
    let hash = id.find('#')?;
    let slash = id[hash..].rfind('/')? + hash;
    if &id[slash + 1..] == "*" {
        return Some(id.to_owned());
    }
    Some(format!("{}/*", &id[..slash]))
}

/// The IDs of one model's nodes, instances and parts.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ModelIds {
    /// The model's ID.
    pub model: String,
    /// Per `PNode` (the model's node order).
    pub nodes: Vec<String>,
    /// Per instance (the model's instance order): its ID (`#<node>/*`).
    pub instances: Vec<String>,
    /// Per instance, per entry of its segment list: the part ID.
    pub parts: Vec<Vec<String>>,
}

impl ModelIds {
    /// The IDs of `model`, read from the archive at `archive_path`.
    #[must_use]
    pub fn of(archive_path: &str, model: &ffx_phyre::Model) -> Self {
        let id = model_id(archive_path);
        let node_names: Vec<&str> = model.nodes.iter().map(|n| n.name.as_str()).collect();
        let nodes = dedupe(&node_names)
            .into_iter()
            .map(|n| format!("{id}#{n}"))
            .collect();
        let (instances, parts) = if model.instances.is_empty() {
            let parts = (0..model.segments.len())
                .map(|s| format!("{id}#mesh/{s}"))
                .collect();
            (vec![format!("{id}#mesh/*")], vec![parts])
        } else {
            let names: Vec<&str> = model
                .instances
                .iter()
                .map(|inst| {
                    inst.node
                        .and_then(|n| model.nodes.get(n))
                        .map_or(inst.name.as_str(), |n| n.name.as_str())
                })
                .collect();
            let names = dedupe(&names);
            let instances = names.iter().map(|n| format!("{id}#{n}/*")).collect();
            let parts = model
                .instances
                .iter()
                .zip(&names)
                .map(|(inst, n)| {
                    (0..inst.segments.len())
                        .map(|k| format!("{id}#{n}/{k}"))
                        .collect()
                })
                .collect();
            (instances, parts)
        };
        Self {
            model: id,
            nodes,
            instances,
            parts,
        }
    }
}

/// One texture a model samples ([`texture_uses`]): its ID, the samplers it
/// is bound to, and the parts drawn with it.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TextureUse {
    pub texture: String,
    pub samplers: Vec<String>,
    pub parts: Vec<String>,
}

/// Which parts sample which textures: each segment of each instance (or
/// each segment, for a model without instances) with its material (the
/// instance's material set, else the segment's own), each of that
/// material's textures. In the order the textures are first met; a part
/// and a sampler are listed once per texture.
#[must_use]
pub fn texture_uses(model: &ffx_phyre::Model, ids: &ModelIds) -> Vec<TextureUse> {
    let mut out: Vec<TextureUse> = Vec::new();
    let mut at: HashMap<String, usize> = HashMap::new();
    let mut add = |material: Option<usize>, part: &str| {
        let Some(m) = material.and_then(|m| model.materials.get(m)) else {
            return;
        };
        for t in &m.textures {
            let id = texture_id(&t.path);
            let i = *at.entry(id.clone()).or_insert_with(|| {
                out.push(TextureUse {
                    texture: id,
                    ..TextureUse::default()
                });
                out.len() - 1
            });
            let u = &mut out[i];
            if !u.samplers.contains(&t.sampler) {
                u.samplers.push(t.sampler.clone());
            }
            if !u.parts.iter().any(|p| p == part) {
                u.parts.push(part.to_owned());
            }
        }
    };
    if model.instances.is_empty() {
        for (s, seg) in model.segments.iter().enumerate() {
            if let Some(part) = ids.parts.first().and_then(|p| p.get(s)) {
                add(seg.material, part);
            }
        }
    } else {
        for (i, inst) in model.instances.iter().enumerate() {
            for (k, &(s, mat)) in inst.segments.iter().enumerate() {
                let Some(part) = ids.parts.get(i).and_then(|p| p.get(k)) else {
                    continue;
                };
                add(
                    mat.or_else(|| model.segments.get(s).and_then(|x| x.material)),
                    part,
                );
            }
        }
    }
    out
}

/// `names` with the second and later of each name suffixed `~1`, `~2`...
fn dedupe(names: &[&str]) -> Vec<String> {
    let mut seen: HashMap<&str, usize> = HashMap::new();
    names
        .iter()
        .map(|&n| {
            let k = seen.entry(n).or_insert(0);
            let out = if *k == 0 {
                n.to_owned()
            } else {
                format!("{n}~{k}")
            };
            *k += 1;
            out
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn forms() {
        assert_eq!(
            model_id("FFX_Data/GameData/PS3Data/map/bsil/bsil00/mdl/d3d11/bsil00.dae.phyre"),
            "x:/ffx_data/gamedata/ps3data/map/bsil/bsil00/mdl/d3d11/bsil00.dae.phyre"
        );
        assert_eq!(
            texture_id("PS3Data/map/bsil/bsil00/tex/Sand.dds"),
            "x:/ffx_data/gamedata/ps3data/map/bsil/bsil00/tex/d3d11/sand.dds.phyre"
        );
        assert_eq!(map_id("BSIL0000"), "x:map/bsil0000");
        assert_eq!(level_id("/map/bsil/bsil00/"), "x:level/bsil00");
        assert_eq!(level_key("/btlmap/bika/bika00_a/"), "bika00_a");
        assert_eq!(level_key("bsil00"), "bsil00");
        let p = "x:/a/b.dae.phyre#FBXASC049_1Shape/3";
        assert_eq!(
            instance_of(p).as_deref(),
            Some("x:/a/b.dae.phyre#FBXASC049_1Shape/*")
        );
        assert_eq!(
            instance_of("x:/a/b.dae.phyre#n/*").as_deref(),
            Some("x:/a/b.dae.phyre#n/*")
        );
        assert_eq!(instance_of("x:/a/b.dae.phyre"), None);
    }

    #[test]
    fn repeated_names_get_a_number() {
        assert_eq!(dedupe(&["a", "b", "a", "a"]), ["a", "b", "a~1", "a~2"]);
    }

    #[test]
    fn texture_uses_follow_the_instances_materials() {
        use ffx_phyre::{Instance, Material, MaterialTexture, Model, Segment};
        let tex = |sampler: &str, path: &str| MaterialTexture {
            sampler: sampler.into(),
            path: path.into(),
        };
        let mut model = Model::default();
        model.materials = vec![
            Material {
                textures: vec![tex("TextureSampler", "PS3Data/map/a/b/tex/Sand.dds")],
                ..Material::default()
            },
            Material {
                textures: vec![
                    tex("TextureSampler", "PS3Data/map/a/b/tex/rock.dds"),
                    tex("NormalMapSampler", "PS3Data/map/a/b/tex/sand.dds"),
                ],
                ..Material::default()
            },
        ];
        model.segments = vec![
            Segment {
                material: Some(0),
                ..Segment::default()
            },
            Segment::default(),
        ];
        let inst = |name: &str, segments| Instance {
            name: name.into(),
            node: None,
            segments,
            skeleton: None,
            bounds: None,
        };
        // the second instance's set gives segment 0 material 1, segment 1 its own (none)
        model.instances = vec![
            inst("i", vec![(0, None), (1, None)]),
            inst("j", vec![(0, Some(1))]),
        ];
        let path = "FFX_Data/GameData/PS3Data/map/a/b/mdl/d3d11/b.dae.phyre";
        let uses = texture_uses(&model, &ModelIds::of(path, &model));
        let sand = "x:/ffx_data/gamedata/ps3data/map/a/b/tex/d3d11/sand.dds.phyre";
        let rock = "x:/ffx_data/gamedata/ps3data/map/a/b/tex/d3d11/rock.dds.phyre";
        let m = model_id(path);
        assert_eq!(uses.len(), 2);
        assert_eq!(uses[0].texture, sand);
        assert_eq!(uses[0].samplers, ["TextureSampler", "NormalMapSampler"]);
        assert_eq!(uses[0].parts, [format!("{m}#i/0"), format!("{m}#j/0")]);
        assert_eq!(uses[1].texture, rock);
        assert_eq!(uses[1].parts, [format!("{m}#j/0")]);
    }
}
