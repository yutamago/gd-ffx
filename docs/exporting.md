# Exporting

gd-ffx exports as a standalone program for Windows x86_64 and Linux x86_64
(Linux covers the Steam Deck). The player doesn't need Godot, .NET or Rust,
but does need their own copy of the game.

```bash
tools/capped.sh tools/export.sh [linux|windows|all]     # a development export (see "Licences")
FFX_GD_FEATURES=none tools/capped.sh tools/export.sh all   # an export without Unicorn
```

Exports are heavy (two release builds of ffx-rust and Godot's export), so
run them inside the shared memory cap (`tools/capped.sh`, docs/building.md,
"Memory"), one at a time. Paths with spaces (the repository, the home folder,
`FFX_RUST_DIR`) aren't supported: the scripts refuse them.

## What a player gets

```
build/linux/                              build/windows/
  gd-ffx.x86_64                             gd-ffx.exe, gd-ffx.console.exe
  libffx_gd.so                              ffx_gd.dll
  data_gd-ffx_linuxbsd_x86_64/              data_gd-ffx_windows_x86_64/
  README.txt                                README.txt
  licences/                                 licences/
```

- The program is Godot's export template with the project (scenes, scripts,
  settings) packed inside it (the PCK embedded).
- `libffx_gd.so` / `ffx_gd.dll` is the extension: ffx-rust, the game's logic.
- `data_gd-ffx_*` holds the C# assembly and the .NET runtime (self-contained:
  no .NET install needed).
- `README.txt` (from `tools/export/README.txt`) tells the player how to point
  it at their game, and `licences/` holds the notices (see "Licences").

About 210 MB for Linux and 230 MB for Windows. Nothing of the game is in an
export: the game folder stays user-set (`--game-dir=`, `FFX_GAME_DIR`, or
`ffx/game/game_dir` in an `override.cfg` beside the program;
docs/running.md), and the classic content is imported on the player's machine
into Godot's user data folder (`user://classic`). `export_presets.cfg` also
excludes a stale `res://classic/` folder.

The Linux export needs glibc 2.34 or newer, which the Steam Deck's SteamOS 3
has. It hasn't been tried on a Deck yet. Run it headless like the editor
build:

```bash
FFX_GAME_DIR=/path/to/game ./build/linux/gd-ffx.x86_64 --headless -- --selftest --documents=/tmp/gd-ffx-docs
```

## What you need

- Everything in docs/building.md (Rust, cargo-xwin for Windows, the .NET SDK,
  Godot 4.7.2 .NET).
- **Godot's .NET export templates**, matching the editor exactly:
  `Godot_v4.7.2-stable_mono_export_templates.tpz` from
  <https://github.com/godotengine/godot/releases/tag/4.7.2-stable>. Check it
  against the release's `SHA512-SUMS.txt`, then unpack its `templates/`
  folder as `~/.local/share/godot/export_templates/4.7.2.stable.mono/`
  (Windows: `%APPDATA%\Godot\export_templates\4.7.2.stable.mono\`). The
  editor's Manage Export Templates dialog does the same.
- Network access the first time for each platform: Godot's .NET export
  publishes the C# project for `linux-x64` / `win-x64`, which fetches the
  .NET runtime pack from NuGet.

## What tools/export.sh does

First, it refuses to export when `godot/` holds files git doesn't track
(`git status --ignored`). The presets pack every resource under `godot/`,
so an untracked or ignored file would end up in the export. Only Godot's
`.godot/`, the generated `.sln`, a `.uid` file holding nothing but a uid,
and `classic/` (which the presets exclude) are allowed.

Then, for each platform:

1. It builds the extension: a release build into `rust/target/export`
   (`CARGO_TARGET_DIR`), with every build path remapped
   (`--remap-path-prefix`, `-ffile-prefix-map`), so no path of the build
   machine ends up in the library. The development build in `rust/target`
   stays as it is.
2. It builds the C# assembly (`dotnet build -c ExportRelease`) and imports
   the project once if needed.
3. It runs Godot's export: `godot --headless --path godot --export-release
   <Linux|Windows> build/<platform>/<program>`. Godot exits 0 even when a
   step fails, so the script also fails on any `ERROR:` in the log.
4. It adds `README.txt` and `licences/`.
5. It checks the result (`tools/export-files.py check`):
   - the files above are there;
   - there are no game files by name (archives, `.bin`, `.dat`, sound banks,
     saves, ...) and no imported classic content;
   - nothing of the build machine appears, in ASCII or UTF-16 and
     case-insensitively: the home folder, the Linux user name or the Windows
     one (`%USERNAME%` via `cmd.exe` on WSL) as whole words, and any user
     folder (`/home/<name>/`, `/mnt/c/Users/<name>/`, `\Users\<name>\`).
     Generic names (Public, Default, and `runner`, the CI builder's home that
     Godot's official templates carry) pass;
   - Unicorn is only in a development export.

### How the extension gets into an export

`godot/ffx_gd.gdextension` names the libraries by path outside `res://`
(`res://../rust/target/...`). Godot's exporter copies them without trouble
and puts the library beside the program, and the exported program loads it
from there by its file name. The presets set the custom feature
`ffx_export`. The `.gdextension` maps `<platform>.<target>.x86_64.ffx_export`
to the build in `rust/target/export`, and Godot picks the entry with the most
matching tags. So exports take `tools/export.sh`'s build, while the editor
and `godot --path godot` keep loading the development build from
`rust/target`. An export from the editor's Export dialog needs
`tools/export.sh` (or `CARGO_TARGET_DIR=rust/target/export
tools/build-extension.sh ...`) to have built that library first.

A .NET export also needs a solution file, `godot/gd-ffx.sln`. The editor
makes one, and `tools/export.sh` writes it when it is missing (gitignored).

## Checked on Windows

The Windows export runs on Windows 11 (this machine's Windows side, from
WSL; 2026-11-16), headless, with a documents sandbox and the classic cache
in a temporary folder and the game read through `\\wsl.localhost\...`
(read only). It passes the same checks as on Linux:

```bat
gd-ffx.console.exe --headless -- --selftest --game-dir=GAME --documents=TMP\docs --classic=TMP\classic
gd-ffx.console.exe --headless -- --selftest-besaid --check-autosave --devsave=7 ...
gd-ffx.console.exe --headless -- --selftest-battle --battle=3,0,0 --devsave=7 ...
gd-ffx.console.exe --headless -- --selftest-menu --menu-screen=abilities --devsave=7 ...
```

The opening reaches the title at 30 updates a second (1,121 updates in 42
s) with the sound at 113 ms; Besaid's map and 12 characters come up and the
game's autosave is read back; the battle's HUD, a damage number and its
effects; the menu's abilities screen. Not checked on Windows yet: a window
(D3D12 or Vulkan rendering) and a pad. From WSL the `.exe` files need the
execute bit (`chmod +x`), or WSL refuses to run them (exit 126).

## Licences

- **The default build (`dev-unicorn`) must not be distributed.** Its
  extension links Unicorn (GPL-2.0), which runs the game functions ffx-rust
  hasn't ported yet. Such an export is for your own use. Its `README.txt`
  starts with "DEVELOPMENT EXPORT: NOT FOR DISTRIBUTION", and its
  `licences/` adds `unicorn-NOTICE.txt` and `unicorn-COPYING`.
- **`FFX_GD_FEATURES=none`** builds without Unicorn: no GPL code, and the
  notices step fails if any GPL-family crate is left in the tree. Until
  ffx-rust's port is complete, such an export stops at the first function
  without a Rust port. ffx-rust's own release notes say that today this
  happens at once, at the game's CRT entry point.
- `licences/` in every export:
  - `LICENSE-MIT`, `LICENSE-APACHE`: gd-ffx (and ffx-rust), MIT OR Apache-2.0;
  - `THIRD-PARTY.txt`: every Rust crate compiled into the extension (from
    `cargo tree`), with its licence, its repository and the licence files it
    ships. This covers godot-rust (gdext, MPL-2.0: its source is
    github.com/godot-rust/gdext and crates.io, at the version listed);
  - `godot-LICENSE.txt`, `godot-COPYRIGHT.txt`: the Godot engine (MIT) and
    its third-party components, as the engine itself reports them
    (`tools/godot-licences.gd`);
  - `dotnet-LICENSE.txt`, `dotnet-THIRD-PARTY-NOTICES.txt`: the .NET runtime
    in the data folder (MIT), from its NuGet runtime pack.

## macOS (not done)

There is no macOS toolchain on the development machine. A macOS export would
need:

- the extension built for macOS: `aarch64-apple-darwin` and
  `x86_64-apple-darwin` (lipo'd into one universal `libffx_gd.dylib`, or a
  `.framework`), on a Mac or with osxcross and the macOS SDK (Apple's licence
  allows it only on Apple hardware). ffx-rust itself has never been built or
  run on macOS (its host emulates the Windows side of the game, so the
  arena's memory mapping and the threads would need checking first);
- `macos.release` / `macos.debug` entries (with `.ffx_export`) in
  `ffx_gd.gdextension`, and a "macOS" preset (the templates already include
  `macos.zip`; .NET exports for macOS are supported);
- code signing and notarization with an Apple Developer ID, or Gatekeeper
  blocks the app (a signed `.dylib` inside the signed `.app`);
- a test Mac: Apple Silicon, plus Intel if `x86_64` is shipped.
