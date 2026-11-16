//! A material's effect variant (`PMaterial.m_effectVariant`, the cluster
//! `ffx_data/gamedata/ps3data/shaders/d3d11/<effect>.fx#<md5>.phyre`) as
//! the glTF export's material extras: its switches, each render pass's
//! fixed-function state (the D3D11 rasterizer, depth-stencil and blend
//! descriptions the game creates the pass with) and the vertex inputs its
//! vertex program reads. Read with ffx-rust's `ffx_scene::effect`
//! (`EffectVariant::parse`); docs/importer.md, "Material states".

use std::fmt::Write as _;

use serde_json::{Value, json};

/// The archive path of an effect variant's cluster from its import id
/// (`PS3Data/Shaders/PhyreDefaultLitShader.fx#0123...` →
/// `ffx_data/gamedata/ps3data/shaders/d3d11/phyredefaultlitshader.fx#0123....phyre`).
#[must_use]
pub fn variant_path(effect: &str) -> String {
    let lower = effect.to_ascii_lowercase();
    let (dir, file) = lower.rsplit_once('/').unwrap_or(("", &lower));
    format!("ffx_data/gamedata/{dir}/d3d11/{file}.phyre")
}

fn word(b: &[u8], at: usize) -> u32 {
    b.get(at..at + 4)
        .map_or(0, |s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
}

/// `D3D11_BLEND` as a name.
fn blend_name(v: u32) -> &'static str {
    match v {
        1 => "ZERO",
        2 => "ONE",
        3 => "SRC_COLOR",
        4 => "INV_SRC_COLOR",
        5 => "SRC_ALPHA",
        6 => "INV_SRC_ALPHA",
        7 => "DEST_ALPHA",
        8 => "INV_DEST_ALPHA",
        9 => "DEST_COLOR",
        10 => "INV_DEST_COLOR",
        11 => "SRC_ALPHA_SAT",
        14 => "BLEND_FACTOR",
        15 => "INV_BLEND_FACTOR",
        16 => "SRC1_COLOR",
        17 => "INV_SRC1_COLOR",
        18 => "SRC1_ALPHA",
        19 => "INV_SRC1_ALPHA",
        _ => "?",
    }
}

/// `D3D11_BLEND_OP` as a name.
fn op_name(v: u32) -> &'static str {
    match v {
        1 => "ADD",
        2 => "SUBTRACT",
        3 => "REV_SUBTRACT",
        4 => "MIN",
        5 => "MAX",
        _ => "?",
    }
}

/// `D3D11_COMPARISON_FUNC` as a name.
fn func_name(v: u32) -> &'static str {
    match v {
        1 => "NEVER",
        2 => "LESS",
        3 => "EQUAL",
        4 => "LESS_EQUAL",
        5 => "GREATER",
        6 => "NOT_EQUAL",
        7 => "GREATER_EQUAL",
        8 => "ALWAYS",
        _ => "?",
    }
}

/// One render pass's state as JSON:
/// `{"pass", "cull" (NONE/FRONT/BACK), "front_ccw", "depth_test",
/// "depth_write", "depth_func", "alpha_to_coverage", "blend"}`, `blend`
/// `null` when render target 0 does not blend, else
/// `{"src", "dst", "op", "src_alpha", "dst_alpha", "op_alpha", "write_mask"}`;
/// [`variant_extras`] adds `"ps_reads"`, the constants its pixel program
/// reads (`AlphaThreshold`: an alpha test; `fgEff`: fog; ...).
fn pass_json(pass_type: &str, s: &ffx_scene::effect::PassState) -> Value {
    let r = &s.raster;
    let d = &s.depth;
    let b = &s.blend;
    // D3D11_RASTERIZER_DESC: FillMode, CullMode, FrontCounterClockwise, ...
    let cull = match word(r, 4) {
        1 => "NONE",
        2 => "FRONT",
        _ => "BACK",
    };
    // D3D11_DEPTH_STENCIL_DESC: DepthEnable, DepthWriteMask, DepthFunc, ...
    // D3D11_BLEND_DESC: AlphaToCoverageEnable, IndependentBlendEnable, then
    // RenderTarget[0]: BlendEnable, SrcBlend, DestBlend, BlendOp,
    // SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha, RenderTargetWriteMask
    let rt = 8;
    let blend = (word(b, rt) != 0).then(|| {
        json!({
            "src": blend_name(word(b, rt + 4)),
            "dst": blend_name(word(b, rt + 8)),
            "op": op_name(word(b, rt + 12)),
            "src_alpha": blend_name(word(b, rt + 16)),
            "dst_alpha": blend_name(word(b, rt + 20)),
            "op_alpha": op_name(word(b, rt + 24)),
            "write_mask": word(b, rt + 28) & 0xff,
        })
    });
    json!({
        "pass": pass_type,
        "cull": cull,
        "front_ccw": word(r, 8) != 0,
        "depth_test": word(d, 0) != 0,
        "depth_write": word(d, 0) != 0 && word(d, 4) != 0,
        "depth_func": func_name(word(d, 8)),
        "alpha_to_coverage": word(b, 0) != 0,
        "blend": blend,
    })
}

/// The names of the constants a program reads (its DXBC reflection's
/// `$Globals` variables marked used), sorted; empty when it has none.
fn constants_read(code: &[u8]) -> Vec<String> {
    let Ok(c) = ffx_dxbc::Container::parse(code) else {
        return Vec::new();
    };
    let Ok(Some(r)) = c.reflection() else {
        return Vec::new();
    };
    let mut v: Vec<String> = r
        .buffers
        .iter()
        .flat_map(|b| b.variables.iter())
        .filter(|x| x.used)
        .map(|x| x.name.clone())
        .collect();
    v.sort();
    v.dedup();
    v
}

/// An effect variant cluster's bytes as the material extras
/// `{"ffx_switches": {name: value}, "ffx_passes": [pass...], "ffx_inputs":
/// ["POSITION0", "COLOR0", "TEXCOORD0", ...], "ffx_input_types": [the
/// stream render type each input reads]}`: one entry per render pass
/// (its first shader's first pass: the pass states are the same for all of
/// a render pass's shaders on the maps), and the vertex inputs of the
/// first render pass's vertex program.
///
/// # Errors
/// When the cluster does not parse.
pub fn variant_extras(bytes: Vec<u8>) -> Result<Value, String> {
    let v = ffx_scene::effect::EffectVariant::parse(bytes).map_err(|e| e.to_string())?;
    let switches: serde_json::Map<String, Value> = v
        .switches
        .iter()
        .map(|(n, val)| (n.clone(), json!(val)))
        .collect();
    let mut passes = Vec::new();
    let mut inputs: Vec<String> = Vec::new();
    let mut types: Vec<String> = Vec::new();
    for rp in &v.passes {
        let Some(p) = rp.shaders.first().and_then(|s| s.passes.first()) else {
            continue;
        };
        let mut pass = pass_json(&rp.pass_type, &p.state);
        pass["ps_reads"] = json!(constants_read(&p.ps.code));
        passes.push(pass);
        if inputs.is_empty() {
            for i in &p.vs.inputs {
                inputs.push(format!("{}{}", i.semantic, i.index));
                types.push(i.render_type.clone());
            }
        }
    }
    Ok(json!({
        "ffx_switches": switches,
        "ffx_passes": passes,
        "ffx_inputs": inputs,
        "ffx_input_types": types,
    }))
}

/// The first render pass's pixel program of a variant as WGSL (ffx-rust's
/// `ffx_dxbc::wgsl` translation of the game's bytecode), for reading a
/// shader's math (`ffx-import effect --wgsl`); `None` when it has none or
/// does not translate.
#[must_use]
pub fn pixel_wgsl(bytes: Vec<u8>, pass_type: Option<&str>) -> Option<String> {
    let v = ffx_scene::effect::EffectVariant::parse(bytes).ok()?;
    let rp = match pass_type {
        Some(t) => v.pass(t)?,
        None => v.passes.first()?,
    };
    let p = rp.shaders.first()?.passes.first()?;
    let vs = ffx_dxbc::Container::parse(&p.vs.code).ok()?;
    let ps = ffx_dxbc::Container::parse(&p.ps.code).ok()?;
    let varyings = ffx_dxbc::wgsl::link(&vs, &ps).ok()?;
    let v = ffx_dxbc::wgsl::translate(&vs, &varyings, 0).ok()?;
    let f = ffx_dxbc::wgsl::translate(&ps, &varyings, 1).ok()?;
    let mut out = String::new();
    for (name, c, code) in [("vertex", &vs, &v.wgsl), ("pixel", &ps, &f.wgsl)] {
        let _ = writeln!(out, "// {name}");
        if let Ok(Some(r)) = c.reflection() {
            for b in &r.bindings {
                let _ = writeln!(out, "// bind {:?} {} slot {}", b.kind, b.name, b.slot);
            }
            for cb in &r.buffers {
                for x in &cb.variables {
                    let _ = writeln!(
                        out,
                        "// cb {} +{} {}{}",
                        cb.name,
                        x.offset,
                        x.name,
                        if x.used { "" } else { " (unused)" }
                    );
                }
            }
        }
        out.push_str(code);
        out.push('\n');
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn effect_ids_as_archive_paths() {
        assert_eq!(
            variant_path("PS3Data/Shaders/PhyreDefaultLitShader.fx#00AB"),
            "ffx_data/gamedata/ps3data/shaders/d3d11/phyredefaultlitshader.fx#00ab.phyre"
        );
    }

    #[test]
    fn pass_states_from_the_d3d11_descriptions() {
        let mut raster = vec![0u8; 40];
        raster[4] = 1; // cull none
        let mut depth = vec![0u8; 52];
        depth[0] = 1;
        depth[4] = 1;
        depth[8] = 4;
        let mut blend = vec![0u8; 264];
        for (k, v) in [1u8, 5, 6, 1, 2, 1, 1, 0x0f].iter().enumerate() {
            blend[8 + 4 * k] = *v;
        }
        let s = ffx_scene::effect::PassState {
            important: 0,
            raster,
            depth,
            blend,
            stencil_ref: 0,
        };
        let j = pass_json("Transparent", &s);
        assert_eq!(j["cull"], "NONE");
        assert_eq!(j["depth_write"], true);
        assert_eq!(j["depth_func"], "LESS_EQUAL");
        assert_eq!(j["blend"]["src"], "SRC_ALPHA");
        assert_eq!(j["blend"]["dst"], "INV_SRC_ALPHA");
        assert_eq!(j["blend"]["op"], "ADD");
        assert_eq!(j["blend"]["dst_alpha"], "ZERO");
    }
}
