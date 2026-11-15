//! Writes the game-state API's C# bindings (`ffx_state_cs::OUTPUT`).

fn main() -> std::io::Result<()> {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let path = root.join(ffx_state_cs::OUTPUT);
    std::fs::write(&path, ffx_state_cs::generate())?;
    println!("wrote {}", ffx_state_cs::OUTPUT);
    Ok(())
}
