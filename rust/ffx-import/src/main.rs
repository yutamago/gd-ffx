//! `ffx-import`: the classic importer from the command line.
//!
//! ```text
//! ffx-import --game-dir DIR --out DIR map <level>...     # /map/znkd/znkd06/, bsil0000
//! ffx-import --game-dir DIR --out DIR map2d <level>...   # a map's 2D layers (/map/kami/kami01/)
//! ffx-import --game-dir DIR --out DIR chr <model>...     # c001, m009, w041
//! ffx-import --game-dir DIR devsaves                     # the developers' saves: map, entrance, scenario
//! ffx-import --game-dir DIR list <text>...               # archive paths containing the text
//! ffx-import --game-dir DIR model <path or level>...     # a model's nodes, instances, materials
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
        let mut ok = true;
        for n in names {
            ok &= model(&imp, n);
        }
        return if ok {
            ExitCode::SUCCESS
        } else {
            ExitCode::FAILURE
        };
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
