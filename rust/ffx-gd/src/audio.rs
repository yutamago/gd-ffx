//! `FfxAudioOut`, the Godot node that plays the game's own mix: an
//! `AudioStreamPlayer` with an `AudioStreamGenerator` (48,000 Hz stereo)
//! that each frame tops its buffer up to 0.2 s from the bridge's
//! [`AudioQueue`] (`get_frames_available`, `push_buffer`).
//!
//! The mix stays ffx-rust's (its FMOD mixer, sample for sample); Godot only
//! plays it, resampled to the output's rate by Godot's mixer. While this
//! node is in the tree the platform's output runs (`audio_running`), so the
//! game's feeder keeps the queue full; without it (muted, or no node) the
//! feeder follows the wall clock and the mix goes nowhere.
//!
//! The refill follows Godot's frames: a frame slower than the generator's
//! 0.2 s is heard as a gap (counted as the player's skips). A pull on
//! Godot's audio thread (a custom `AudioStreamPlayback`) is G3's next step.
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
/// of two: 16,384 frames).
const BUFFER_SECONDS: f32 = 0.2;
/// Frames the generator is filled to at most: what Godot holds beyond the
/// queue (0.2 s). A frame of Godot's up to that long is no gap; more
/// would only add delay.
const BUFFERED: usize = 9_600;

/// The game's sound, as a node: add it to the tree before the game starts
/// (the output runs only with a player attached). It sets its player up
/// each time it enters the tree and lets it go when it leaves.
#[derive(GodotClass)]
#[class(base = Node)]
pub struct FfxAudioOut {
    base: Base<Node>,
    bridge: Arc<Bridge>,
    player: Option<Gd<AudioStreamPlayer>>,
    playback: Option<Gd<AudioStreamGeneratorPlayback>>,
    /// The generator's ring in frames (the most it ever had free).
    capacity: usize,
    /// The generator's skips counted so far.
    skips: i32,
    /// Frames were pushed since the player was set up (its skips count
    /// from then on, not while the queue fills the first time).
    fed: bool,
    /// The samples of one pull (kept to spare the allocation).
    scratch: Vec<f32>,
    /// The frames of one push (kept likewise).
    frames: PackedVector2Array,
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
            skips: 0,
            fed: false,
            scratch: Vec::new(),
            frames: PackedVector2Array::new(),
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
        let Some(playback) = playback else {
            godot_warn!("ffx: no AudioStreamGenerator playback; the game stays silent");
            player.queue_free();
            return;
        };
        self.capacity = 0;
        self.skips = playback.get_skips();
        self.fed = false;
        self.player = Some(player);
        self.playback = Some(playback);
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
        // the generator's skips: heard as gaps once it has been fed
        let skips = playback.get_skips();
        if self.fed && self.bridge.audio.running() {
            self.bridge
                .audio
                .add_skips(u64::try_from(skips - self.skips).unwrap_or(0));
        }
        self.skips = skips;
        let free = usize::try_from(playback.get_frames_available()).unwrap_or(0);
        self.capacity = self.capacity.max(free);
        let room = BUFFERED.saturating_sub(self.capacity - free);
        self.scratch.clear();
        let n = self.bridge.audio.pull(room, &mut self.scratch);
        if n == 0 {
            return;
        }
        self.frames.resize(n);
        for (d, &[l, r]) in self
            .frames
            .as_mut_slice()
            .iter_mut()
            .zip(self.scratch.as_chunks::<2>().0)
        {
            *d = Vector2::new(l, r);
        }
        playback.push_buffer(&self.frames);
        self.fed = true;
    }

    fn exit_tree(&mut self) {
        if self.playback.take().is_some() {
            self.bridge.audio.attach(false);
        }
        if let Some(mut p) = self.player.take() {
            p.stop();
            p.queue_free();
        }
        // set up again if the node comes back
        self.base_mut().request_ready();
    }
}

#[godot_api]
impl FfxAudioOut {
    /// The output's counters: `running` (the game started its output and
    /// this player pulls), `queued` (frames waiting), `underruns` (the
    /// queue ran dry), `skips` (Godot's player ran empty: heard as gaps),
    /// `dropped` (frames, the queue was full), `pulled` (frames played).
    #[func]
    fn stats(&self) -> VarDictionary {
        let s = self.bridge.audio.stats();
        let int = |v: u64| i64::try_from(v).unwrap_or(i64::MAX);
        vdict! {
            "running" => s.running,
            "queued" => int(s.queued as u64),
            "underruns" => int(s.underruns),
            "skips" => int(s.skips),
            "dropped" => int(s.dropped),
            "pulled" => int(s.pulled),
        }
    }
}
