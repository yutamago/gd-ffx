//! `ffx-import`: the classic importer from the command line.
//!
//! ```text
//! ffx-import --game-dir DIR --out DIR map <level>...     # /map/znkd/znkd06/, bsil0000
//! ffx-import --game-dir DIR --out DIR chr <model>...     # c001, m009, w041
//! ffx-import --game-dir DIR devsaves                     # the developers' saves: map, entrance, scenario
//! ffx-import --game-dir DIR list <text>...               # archive paths containing the text
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
            "chr" => imp.import_character(n, &out),
            other => {
                eprintln!("unknown command {other} (map, chr, devsaves)");
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
