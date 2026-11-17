# Building

gd-ffx has two parts: the GDExtension (`rust/ffx-gd`, Rust) and the Godot
project (`godot/`, C#). Build the extension first; the Godot project loads it
from `rust/target` (`godot/ffx_gd.gdextension`).

## What you need

- **Rust** (stable, 1.94 or newer) with cargo.
- **Godot 4.7 .NET** (the "mono" build), from the official releases:
  <https://github.com/godotengine/godot/releases> (`Godot_v4.7.2-stable_mono_*`).
  Check the download against the release's `SHA512-SUMS.txt`.
- **.NET SDK** 9 or newer (the project targets `net9.0`).
- For the Windows build from Linux/WSL: `cargo-xwin`, clang/LLVM (`clang-cl`,
  `llvm-lib`, `llvm-ml64`), and cmake and ninja (or `uvx` to fetch them).

On the development machine (WSL2 with Windows beside it) Godot lives in two
places: the Linux build under `~/tools/Godot_v4.7.2-stable_mono_linux_x86_64/`
(headless runs and tests; `godot` there is a link to the binary) and the
Windows build under `%USERPROFILE%\tools\godot\Godot_v4.7.2-stable_mono_win64\`.

## ffx-rust

`rust/Cargo.toml` takes ffx-rust's crates (`ffx-host`, `ffx-bridge`) from its
GitHub repository, branch `main`. To build against a local checkout instead (a
branch not merged yet, your own changes), set `FFX_RUST_DIR`:

```bash
export FFX_RUST_DIR=/path/to/ffx-rust
tools/build-extension.sh
```

The script passes `[patch]` entries for the two crates to cargo. Plain cargo
in `rust/` can do the same with a gitignored `.cargo/config.toml` at the
repository's root:

```toml
[patch."https://github.com/yutamago/ffx-rust"]
ffx-host = { path = "/path/to/ffx-rust/crates/ffx-host" }
ffx-bridge = { path = "/path/to/ffx-rust/crates/ffx-bridge" }
```

## The extension

```bash
tools/build-extension.sh                   # Linux: rust/target/release/libffx_gd.so
tools/build-extension.sh windows           # rust/target/x86_64-pc-windows-msvc/release/ffx_gd.dll
tools/build-extension.sh linux clippy -- -D warnings
tools/build-extension.sh linux test
```

The Windows build is cross-built from Linux/WSL with cargo-xwin, the way
ffx-rust builds `ffx-rust.exe` (its `tools/build-host.sh`). It takes
ffx-rust's toolchain shims (`tools/shims`, `tools/xwin-env.sh`) from
`FFX_RUST_DIR` when that is set, otherwise from cargo's checkout of the
ffx-rust revision in `rust/Cargo.lock`.

Always a release build: the game runs inside the extension, and ffx-rust's
executor is far too slow unoptimised (`rust/Cargo.toml` optimises the
dependencies of a debug build too).

### Features

No feature is on by default (since 2026-11-17): the default build has
neither Unicorn nor Ruffle, and gd-ffx draws every screen natively with
Godot (docs/architecture.md, "Native UI"). All 110 developer saves pass
`tools/sweep-devsaves.sh` and every self-check passes with such a build.

- `swf`: the remaster's Flash (Iggy) screens on Ruffle inside ffx-host.
  Heavy (wgpu). gd-ffx shows none of them (the game runs on the null
  graphics device); only for comparing with ffx-rust's own runs. Without it
  the Flash screens are null; the start-up logos are skipped either way.
- `dev-unicorn` (off by default since 2026-11-17): development only, as in
  ffx-rust. Functions of the game without a Rust port run in Unicorn, from
  your own `FFX.exe`. ffx-rust runs the whole game without it now (all 110
  developer saves pass `tools/sweep-devsaves.sh` without Unicorn, 0 errors).
  Unicorn is GPL-2.0: a build with it (`FFX_GD_FEATURES=dev-unicorn`) is
  never given to others.

## The Godot project

```bash
cd godot
dotnet build                     # the C# assembly
~/tools/Godot_v4.7.2-stable_mono_linux_x86_64/godot --headless --editor --quit --path .   # import once
```

The first import registers the extension. Godot 4.7.2 may crash on exit right
after that first registration (seen on Linux, no harm: the import is done);
later runs exit cleanly.

Open `godot/project.godot` in the Godot .NET editor to work on it.

To build a standalone program (Godot's export, with the extension and the
.NET runtime), use `tools/export.sh`: see docs/exporting.md.

## Memory

Builds of ffx-rust, the game runs and the exports are heavy; on the
development machine (WSL2) running out of memory and swap crashes the whole
WSL instance. Heavy commands run through `tools/capped.sh`, inside one
systemd user slice that caps them together:

```ini
# ~/.config/systemd/user/gdffx.slice
[Unit]
Description=gd-ffx / ffx-rust builds, tests and game runs (shared memory cap)

[Slice]
MemoryHigh=22G
MemoryMax=24G
MemorySwapMax=2G
```

`systemctl --user daemon-reload`, then `tools/capped.sh <command>`. The cap
is about half of a 47 GB machine, leaving room for everything else (the
machine stays under 80 %); scale it to yours.
