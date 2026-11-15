//! `FfxAudioOut`, the Godot node that plays the game's own mix: an
//! `AudioStreamPlayer` with an `AudioStreamGenerator` (48,000 Hz stereo)
//! that each frame tops its buffer up to 0.1 s from the bridge's
//! [`AudioQueue`] (`get_frames_available`, `push_buffer`).
//!
//! The mix stays ffx-rust's (its FMOD mixer, sample for sample); Godot only
//! plays it, resampled to the output's rate by Godot's mixer. While this
//! node is in the tree the platform's output runs (`audio_running`), so the
//! game's feeder keeps the queue full; without it (muted, or no node) the
//! feeder follows the wall clock and the mix goes nowhere.
//!
//! [`AudioQueue`]: crate::platform::AudioQueue

use std::sync::Arc;

use godot::classes::audio_stream_generator::AudioStreamGeneratorMixRate;
use godot::classes::node::ProcessMode;
use godot::classes::{
    AudioServer, AudioStreamGenerator, AudioStreamGeneratorPlayback, AudioStreamPlayer, INode, Node,
};
use godot::prelude::*;

use crate::platform::{AUDIO_RATE, Bridge};

/// The generator's buffer in seconds (Godot rounds its ring up to a power
/// of two: 8,192 frames).
const BUFFER_SECONDS: f32 = 0.1;
/// Frames the generator is filled to at most: what Godot holds beyond the
/// queue (0.1 s). Long enough for a slow frame (it is refilled once a
/// frame), short enough to keep the delay small.
const BUFFERED: usize = 4_800;

/// The game's sound, as a node: add it to the tree before the game starts
/// (the output runs only with a player attached).
#[derive(GodotClass)]
#[class(base = Node)]
pub struct FfxAudioOut {
    base: Base<Node>,
    bridge: Arc<Bridge>,
    player: Option<Gd<AudioStreamPlayer>>,
    playback: Option<Gd<AudioStreamGeneratorPlayback>>,
    /// The generator's ring in frames (the most it ever had free).
    capacity: usize,
    /// The samples of one pull (kept to spare the allocation).
    scratch: Vec<f32>,
}

#[godot_api]
impl INode for FfxAudioOut {
    fn init(base: Base<Node>) -> Self {
        Self {
            base,
            bridge: crate::game::process_bridge(),
            player: None,
            playback: None,
            capacity: 0,
            scratch: Vec::new(),
        }
    }

    fn ready(&mut self) {
        // the game plays on when Godot's tree is paused
        self.base_mut().set_process_mode(ProcessMode::ALWAYS);
        let mut generator = AudioStreamGenerator::new_gd();
        generator.set_mix_rate_mode(AudioStreamGeneratorMixRate::CUSTOM);
        #[allow(clippy::cast_precision_loss)] // 48,000 is exact
        generator.set_mix_rate(AUDIO_RATE as f32);
        generator.set_buffer_length(BUFFER_SECONDS);
        let mut player = AudioStreamPlayer::new_alloc();
        player.set_name("Player");
        player.set_stream(&generator);
        player.set_process_mode(ProcessMode::ALWAYS);
        self.base_mut().add_child(&player);
        player.play();
        let playback = player
            .get_stream_playback()
            .and_then(|p| p.try_cast::<AudioStreamGeneratorPlayback>().ok());
        if playback.is_none() {
            godot_warn!("ffx: no AudioStreamGenerator playback; the game stays silent");
            return;
        }
        self.player = Some(player);
        self.playback = playback;
        self.bridge.audio.attach(true);
        let server = AudioServer::singleton();
        godot_print!(
            "ffx: audio: the game's mix through Godot ({} driver, output {} Hz, buffer {} s)",
            server.get_driver_name(),
            server.get_mix_rate(),
            BUFFER_SECONDS
        );
    }

    fn process(&mut self, _delta: f64) {
        let Some(playback) = &mut self.playback else {
            return;
        };
        let free = usize::try_from(playback.get_frames_available()).unwrap_or(0);
        self.capacity = self.capacity.max(free);
        let held = self.capacity - free;
        let room = BUFFERED.saturating_sub(held);
        self.scratch.clear();
        if self.bridge.audio.pull(room, &mut self.scratch) == 0 {
            return;
        }
        let frames: PackedVector2Array = self
            .scratch
            .as_chunks::<2>()
            .0
            .iter()
            .map(|&[l, r]| Vector2::new(l, r))
            .collect();
        playback.push_buffer(&frames);
    }

    fn exit_tree(&mut self) {
        if self.playback.take().is_some() {
            self.bridge.audio.attach(false);
        }
        if let Some(mut p) = self.player.take() {
            p.stop();
        }
    }
}

#[godot_api]
impl FfxAudioOut {
    /// The output's counters: `running` (the game started its output and
    /// this player pulls), `queued` (frames waiting), `underruns`,
    /// `dropped` (frames, the queue was full), `pulled` (frames played),
    /// `skips` (Godot's generator ran empty).
    #[func]
    fn stats(&self) -> VarDictionary {
        let s = self.bridge.audio.stats();
        let skips = self.playback.as_ref().map_or(0, |p| p.get_skips());
        vdict! {
            "running" => s.running,
            "queued" => i64::try_from(s.queued).unwrap_or(i64::MAX),
            "underruns" => i64::try_from(s.underruns).unwrap_or(i64::MAX),
            "dropped" => i64::try_from(s.dropped).unwrap_or(i64::MAX),
            "pulled" => i64::try_from(s.pulled).unwrap_or(i64::MAX),
            "skips" => skips,
        }
    }
}
