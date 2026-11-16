//! `ffx-import`: the classic importer from the command line.
//!
//! ```text
//! ffx-import --game-dir DIR --out DIR map <level>...     # /map/znkd/znkd06/, bsil0000
//! ffx-import --game-dir DIR --out DIR map2d <level>...   # a map's 2D layers (/map/kami/kami01/)
//! ffx-import --game-dir DIR --out DIR chr <model>...     # c001, m009, w041
//! ffx-import --game-dir DIR devsaves                     # the developers' saves: map, entrance, scenario
//! ffx-import --game-dir DIR list <text>...               # archive paths containing the text
//! ffx-import --game-dir DIR model <path or level>...     # a model's nodes, instances, materials
//! ffx-import --game-dir DIR effects <path or level>...   # its materials' effect variants: switches, pass states, inputs
//! ffx-import --game-dir DIR effect [--wgsl [PASS]] <id>  # one variant (PS3Data/Shaders/X.fx#MD5); --wgsl: its programs as WGSL
//! ffx-import --game-dir DIR morphs <path or level>...    # its morphing instances (`all`: a census of every map model)
//! ```
//!
//! `--game-dir` defaults to `FFX_GAME_DIR`. Only reads the game; writes only
//! under `--out`.

use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    let mut args = std::env::args().skip(1);
    let mut game = std::env::var_os("FFX_GAME_DIR").map(PathBuf::from);
    let mut out: Option<PathBuf> = None;
    let mut rest = Vec::new();
    while let Some(a) = args.next() {
        match a.as_str() {
            "--game-dir" => game = args.next().map(PathBuf::from),
            "--out" => out = args.next().map(PathBuf::from),
            _ => rest.push(a),
        }
    }
    let Some(game) = game else {
        eprintln!("no game folder (--game-dir or FFX_GAME_DIR)");
        return ExitCode::FAILURE;
    };
    let imp = match ffx_import::Importer::open(&game) {
        Ok(i) => i,
        Err(e) => {
            eprintln!("{e}");
            return ExitCode::FAILURE;
        }
    };
    let (what, names) = rest
        .split_first()
        .map_or(("", &[][..]), |(w, n)| (w.as_str(), n));
    if what == "devsaves" {
        return devsaves(&imp);
    }
    if what == "model" {
        return each(names, |n| model(&imp, n));
    }
    if what == "effects" {
        return each(names, |n| effects(&imp, n));
    }
    if what == "effect" {
        return effect(&imp, names);
    }
    if what == "morphs" {
        return each(names, |n| {
            if n == "all" {
                morph_census(&imp)
            } else {
                morphs(&imp, n)
            }
        });
    }
    if what == "list" {
        for n in names {
            for p in imp.list(n) {
                println!("{p}");
            }
        }
        return ExitCode::SUCCESS;
    }
    let Some(out) = out else {
        eprintln!("--out DIR is needed");
        return ExitCode::FAILURE;
    };
    let mut ok = true;
    for n in names {
        let r = match what {
            "map" => imp.import_map(n, &out),
            "map2d" => imp.import_map_layers(n, &out),
            "chr" => imp.import_character(n, &out),
            other => {
                eprintln!("unknown command {other} (map, map2d, chr, devsaves, list, model)");
                return ExitCode::FAILURE;
            }
        };
        match r {
            Ok(p) => println!("{n}: {}", p.display()),
            Err(e) => {
                eprintln!("{n}: {e}");
                ok = false;
            }
        }
    }
    if ok {
        ExitCode::SUCCESS
    } else {
        ExitCode::FAILURE
    }
}

/// Runs `f` on each name; fails when any does.
fn each(names: &[String], mut f: impl FnMut(&str) -> bool) -> ExitCode {
    let mut ok = true;
    for n in names {
        ok &= f(n);
    }
    if ok {
        ExitCode::SUCCESS
    } else {
        ExitCode::FAILURE
    }
}

/// Prints the model at archive path `path` (or a level's map model): its
/// nodes, instances with the bounds of their segments' positions, and
/// materials (effect, textures, parameters).
fn model(imp: &ffx_import::Importer, path: &str) -> bool {
    let path = if path.contains(".dae.phyre") {
        path.to_string()
    } else if let Some(p) = imp.map_model(path).or_else(|| imp.map_layers_model(path)) {
        p
    } else {
        eprintln!("{path}: not a model or level");
        return false;
    };
    let m = match imp
        .read(&path)
        .map_err(|e| e.to_string())
        .and_then(|b| ffx_phyre::Model::parse(&b).map_err(|e| e.to_string()))
    {
        Ok(m) => m,
        Err(e) => {
            eprintln!("{path}: {e}");
            return false;
        }
    };
    println!("{path}");
    for (i, n) in m.nodes.iter().enumerate() {
        let t = &n.local;
        println!(
            "node {i} {:?} parent {:?} diag ({}, {}, {}) t ({}, {}, {})",
            n.name, n.parent, t[0], t[5], t[10], t[12], t[13], t[14]
        );
    }
    for (i, inst) in m.instances.iter().enumerate() {
        println!("instance {i} {:?} node {:?}", inst.name, inst.node);
        for &(s, mat) in &inst.segments {
            let seg = &m.segments[s];
            let mut lo = [f32::MAX; 3];
            let mut hi = [f32::MIN; 3];
            for p in &seg.positions {
                for k in 0..3 {
                    lo[k] = lo[k].min(p[k]);
                    hi[k] = hi[k].max(p[k]);
                }
            }
            let (mut ulo, mut uhi) = ([f32::MAX; 2], [f32::MIN; 2]);
            for uv in &seg.uvs {
                for k in 0..2 {
                    ulo[k] = ulo[k].min(uv[k]);
                    uhi[k] = uhi[k].max(uv[k]);
                }
            }
            let mut colours: Vec<[u8; 4]> = seg.colors.clone();
            colours.sort_unstable();
            colours.dedup();
            colours.truncate(4);
            println!(
                "  segment {s} material {mat:?} verts {} tris {} pos {lo:?}..{hi:?} uv {ulo:?}..{uhi:?} colours {colours:?}",
                seg.positions.len(),
                seg.indices.len() / 3,
            );
        }
    }
    for (i, mat) in m.materials.iter().enumerate() {
        println!("material {i} effect {:?}", mat.effect);
        for t in &mat.textures {
            println!(
                "  {} {} ({})",
                t.sampler,
                t.path,
                if imp.has(&t.archive_path()) {
                    "found"
                } else {
                    "missing"
                }
            );
        }
        for p in &mat.parameters {
            println!("  {} {:?}", p.name, p.values);
        }
    }
    true
}

/// Reads a model (an archive path or a level's map model).
fn load_model(imp: &ffx_import::Importer, path: &str) -> Option<(String, ffx_phyre::Model)> {
    let path = if path.contains(".dae.phyre") {
        path.to_string()
    } else if let Some(p) = imp.map_model(path).or_else(|| imp.map_layers_model(path)) {
        p
    } else {
        eprintln!("{path}: not a model or level");
        return None;
    };
    match imp
        .read(&path)
        .map_err(|e| e.to_string())
        .and_then(|b| ffx_phyre::Model::parse(&b).map_err(|e| e.to_string()))
    {
        Ok(m) => Some((path, m)),
        Err(e) => {
            eprintln!("{path}: {e}");
            None
        }
    }
}

/// Prints each material's effect variant as the import exports it
/// (switches, pass states, vertex inputs), and each instance's render order
/// and materials.
fn effects(imp: &ffx_import::Importer, path: &str) -> bool {
    let Some((path, m)) = load_model(imp, path) else {
        return false;
    };
    println!("{path}");
    for (i, inst) in m.instances.iter().enumerate() {
        let mats: Vec<Option<usize>> = inst.segments.iter().map(|&(_, mat)| mat).collect();
        println!(
            "instance {i} {:?} render order {:?} materials {mats:?}",
            inst.name,
            inst.bounds.map(|b| b.render_order)
        );
    }
    for (i, mat) in m.materials.iter().enumerate() {
        let fx = mat.effect.as_deref().unwrap_or("");
        println!(
            "material {i} {fx} tex {:?}",
            mat.base_color().map(|t| &t.path)
        );
        match imp.effect_extras(fx) {
            Some(x) => println!("  {x}"),
            None => println!("  (no variant)"),
        }
    }
    true
}

/// Prints a model's instances with morph tags (`m_mimeCt`, `m_dObjFlag`,
/// `m_objID`) and its morphing instances' inputs: per segment, the inputs'
/// render types and widths, how far the second moves from the first, and
/// whether the first is the segment's stored `Vertex` stream.
fn morphs(imp: &ffx_import::Importer, path: &str) -> bool {
    let Some((path, m)) = load_model(imp, path) else {
        return false;
    };
    let Ok(bytes) = imp.read(&path) else {
        return false;
    };
    println!("{path}");
    let tags = ffx_import::morph::tags(&bytes);
    for (i, t) in tags.iter().enumerate() {
        if t.mime_ct != 0 || t.dobj_flag != 0 {
            let name = m.instances.get(i).map_or("", |x| x.name.as_str());
            println!(
                "instance {i} {name:?} mime_ct {} dobj_flag {:#x} obj_id {}",
                t.mime_ct, t.dobj_flag, t.obj_id
            );
        }
    }
    for mo in ffx_import::morph::read(&bytes) {
        let inst = m.instances.get(mo.instance);
        println!(
            "morph instance {} {:?} modifier {} mime_ct {:?}",
            mo.instance,
            inst.map_or("", |x| x.name.as_str()),
            mo.modifier,
            tags.get(mo.instance).map(|t| t.mime_ct)
        );
        for s in &mo.segments {
            let stored = inst
                .and_then(|x| x.segments.get(s.k))
                .and_then(|&(g, _)| m.segments.get(g));
            let kinds: Vec<String> = s
                .inputs
                .iter()
                .map(|x| format!("{}x{}", x.semantic, x.width))
                .collect();
            let spread = match (s.inputs.first(), s.inputs.get(1)) {
                (Some(a), Some(b)) => a
                    .values
                    .iter()
                    .zip(&b.values)
                    .map(|(x, y)| (x - y).abs())
                    .fold(0f32, f32::max),
                _ => 0.0,
            };
            let base = match (stored, s.inputs.first()) {
                (Some(seg), Some(a)) if a.width == 3 && seg.positions.len() == s.vertices => seg
                    .positions
                    .iter()
                    .flatten()
                    .zip(&a.values)
                    .map(|(x, y)| (x - y).abs())
                    .fold(0f32, f32::max),
                _ => f32::NAN,
            };
            println!(
                "  segment {} verts {} inputs {kinds:?} max |in1 - in0| {spread} max |stored - in0| {base}",
                s.k, s.vertices
            );
        }
    }
    true
}

/// Every map model's morphing instances, summed: `m_mimeCt` values,
/// modifiers, input kinds, and instances whose network does not read.
fn morph_census(imp: &ffx_import::Importer) -> bool {
    use std::collections::BTreeMap;
    use std::fmt::Write;
    let mut mime: BTreeMap<i32, usize> = BTreeMap::new();
    let mut kinds: BTreeMap<String, usize> = BTreeMap::new();
    let (mut maps, mut dynamic, mut read, mut networks) = (0, 0, 0, 0);
    let mut examples: [Vec<String>; 3] = Default::default();
    for path in imp.list("/mdl/d3d11/") {
        if !path.contains("ps3data/map/") || !path.ends_with(".dae.phyre") {
            continue;
        }
        let Ok(bytes) = imp.read(&path) else {
            continue;
        };
        maps += 1;
        let tags = ffx_import::morph::tags(&bytes);
        for t in &tags {
            *mime.entry(t.mime_ct).or_default() += 1;
        }
        // a few maps of each other kind: UV morphs, colour morphs, UV offsets
        for (k, hit) in [
            tags.iter().any(|t| t.mime_ct == 3),
            tags.iter().any(|t| t.mime_ct == 4),
            tags.iter().any(|t| t.dobj_flag & 0x400 != 0),
        ]
        .into_iter()
        .enumerate()
        {
            if hit && examples[k].len() < 3 {
                examples[k].push(file_stem_of(&path));
            }
        }
        let morphs = ffx_import::morph::read(&bytes);
        read += morphs.len();
        if let Ok(c) = ffx_phyre::cluster::Cluster::parse(&bytes) {
            networks += c
                .list("PModifierNetworkDynamicMeshInstance")
                .map_or(0, |l| l.count as usize);
        }
        dynamic += tags.iter().filter(|t| t.mime_ct == 1).count();
        for mo in &morphs {
            let mut k = format!(
                "{} mime {:?}",
                mo.modifier,
                tags.get(mo.instance).map(|t| t.mime_ct)
            );
            for s in mo.segments.iter().take(1) {
                for x in &s.inputs {
                    let _ = write!(k, " {}x{}", x.semantic, x.width);
                }
            }
            *kinds.entry(k).or_default() += 1;
        }
    }
    println!("{maps} map models; instances by m_mimeCt: {mime:?}");
    println!("m_mimeCt 1: {dynamic}; dynamic mesh instances: {networks}; morphs read: {read}");
    for (k, n) in kinds {
        println!("  {n:5} {k}");
    }
    println!(
        "e.g. m_mimeCt 3: {:?}; 4: {:?}; m_dObjFlag 0x400: {:?}",
        examples[0], examples[1], examples[2]
    );
    true
}

/// `.../<name>.dae.phyre`'s name.
fn file_stem_of(path: &str) -> String {
    let name = path.rsplit('/').next().unwrap_or(path);
    name.strip_suffix(".dae.phyre").unwrap_or(name).to_string()
}

/// Prints one effect variant's extras, or with `--wgsl [PASS]` its first
/// (or that) render pass's programs as WGSL.
fn effect(imp: &ffx_import::Importer, args: &[String]) -> ExitCode {
    let (wgsl, pass, id) = match args {
        [w, id] if w == "--wgsl" => (true, None, id),
        [w, p, id] if w == "--wgsl" => (true, Some(p.as_str()), id),
        [id] => (false, None, id),
        _ => {
            eprintln!("effect [--wgsl [PASS]] <PS3Data/Shaders/X.fx#MD5>");
            return ExitCode::FAILURE;
        }
    };
    if wgsl {
        let Ok(bytes) = imp.read(&ffx_import::effect::variant_path(id)) else {
            eprintln!("{id}: not in the archive");
            return ExitCode::FAILURE;
        };
        let Some(s) = ffx_import::effect::pixel_wgsl(bytes, pass) else {
            eprintln!("{id}: no programs that translate");
            return ExitCode::FAILURE;
        };
        println!("{s}");
    } else {
        let Some(x) = imp.effect_extras(id) else {
            eprintln!("{id}: not in the archive");
            return ExitCode::FAILURE;
        };
        println!("{}", serde_json::to_string_pretty(&x).unwrap_or_default());
    }
    ExitCode::SUCCESS
}

/// The developers' saves (`savesforviewer/1..110`): the save data block at
/// `+0x40` holds the map and entrance the load jumps to at `+0xba` and
/// `+0xb8`, the scenario at `+0xbec` (ffx-rust's docs/harness.md,
/// "Developer saves").
fn devsaves(imp: &ffx_import::Importer) -> ExitCode {
    for n in 1..=110 {
        let path = format!("ffx_data/gamedata/ps3data/savesforviewer/{n}");
        let Ok(b) = imp.read(&path) else {
            println!("{n:3}: missing");
            continue;
        };
        let u16_at = |o: usize| {
            b.get(o..o + 2)
                .map_or(0, |s| u16::from_le_bytes([s[0], s[1]]))
        };
        let block = 0x40;
        println!(
            "{n:3}: map {:#06x} entrance {:#04x} scenario {}",
            u16_at(block + 0xba),
            u16_at(block + 0xb8),
            u16_at(block + 0xbec)
        );
    }
    ExitCode::SUCCESS
}
