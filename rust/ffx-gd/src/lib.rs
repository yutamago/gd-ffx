//! `ffx-gd`: ffx-rust, the game-logic core of FINAL FANTASY X (Steam HD
//! Remaster), as a Godot `GDExtension` (docs/architecture.md).
//!
//! - [`platform`]: `GodotPlatform`, ffx-rust's platform seam answered from
//!   Godot (window events, keyboard, mouse, pads; the null graphics device);
//! - [`game`]: `FfxGame`, the node that boots the game from the player's
//!   own copy and runs it on its own thread at 30 updates a second;
//! - [`snapshots`]: the game-state API's snapshots (ffx-rust's
//!   `ffx-state`), the newest two, for Godot to interpolate between;
//! - [`state`]: the few state values the G0 debug view reads;
//! - [`keys`]: Godot's keys as the game's scan codes.
//!
//! Nothing of the game is built in: it is read from the player's folder at
//! run time.

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
