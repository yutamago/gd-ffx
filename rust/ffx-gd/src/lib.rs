//! `ffx-gd`: ffx-rust, the game-logic core of FINAL FANTASY X (Steam HD
//! Remaster), as a Godot `GDExtension` (docs/architecture.md).
//!
//! - [`platform`]: `GodotPlatform`, ffx-rust's platform seam answered from
//!   Godot (window events, keyboard, mouse, pads; the null graphics device;
//!   the queue of the game's sound mix);
//! - [`classic`]: the game's maps and characters imported from the
//!   player's archive (`ffx-import`) in the background;
//! - [`game`]: `FfxGame`, the node that boots the game from the player's
//!   own copy and runs it on its own thread at 30 updates a second;
//! - [`audio`]: `FfxAudioOut`, the node that plays the game's own mix
//!   through Godot's audio;
//! - [`snapshots`]: the game-state API's snapshots (ffx-rust's
//!   `ffx-state`), the newest two, for Godot to interpolate between;
//! - [`state`]: the few state values the G0 debug view reads;
//! - [`keys`]: Godot's keys as the game's scan codes;
//! - [`esc`]: the ESC menu's Flash callbacks, called for gd-ffx's native page;
//! - [`encounters`]: the battle maps of the current map's random encounters.
//!
//! Nothing of the game is built in: it is read from the player's folder at
//! run time.

pub mod audio;
pub mod classic;
pub mod encounters;
pub mod esc;
pub mod game;
pub mod keys;
pub mod platform;
pub mod snapshots;
pub mod state;

use godot::prelude::*;

struct FfxGd;

// SAFETY: the one entry point of the library, as gdext requires.
#[gdextension]
unsafe impl ExtensionLibrary for FfxGd {}
