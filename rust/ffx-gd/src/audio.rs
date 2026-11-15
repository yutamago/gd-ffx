//! `FfxAudioOut`, the Godot node that plays the game's own mix: an
//! `AudioStreamPlayer` with an [`FfxAudioStream`], whose playback
//! ([`FfxAudioPlayback`], an `AudioStreamPlaybackResampled` at 48,000 Hz
//! stereo) takes frames from the bridge's [`AudioQueue`] on Godot's audio
//! thread exactly as Godot's mix asks for them (G3 step 2, the pull
//! model: as WASAPI's event does for ffx-rust on Windows).
//!
//! The mix stays ffx-rust's (its FMOD mixer, sample for sample); Godot only
//! plays it, resampled to the output's rate by Godot's resampler when that
//! is not 48,000 Hz. While this node is in the tree the platform's output
//! runs (`audio_running`), so the game's feeder keeps the queue full;
//! without it (muted, or no node) the feeder follows the wall clock and the
//! mix goes nowhere.
//!
//! No buffer of ours lies between the queue and Godot's mix (the
//! resampler holds at most one 128-frame chunk), so the queue is all the
//! delay on our side and `audio_queued` tells the feeder all of it; nothing
//! follows Godot's frame rate.
//!
//! [`AudioQueue`]: crate::platform::AudioQueue

use std::sync::Arc;

use godot::classes::native::AudioFrame;
use godot::classes::node::ProcessMode;
use godot::classes::{
    AudioServer, AudioStreamPlayback, AudioStreamPlaybackResampled, AudioStreamPlayer,
    IAudioStream, IAudioStreamPlaybackResampled, INode, Node,
};
use godot::meta::conv::RawPtr;
use godot::prelude::*;

use crate::platform::{AUDIO_RATE, Bridge};

/// The game's mix as a Godot stream: each playback takes from the
/// process's bridge.
#[derive(GodotClass)]
#[class(base = AudioStream, init)]
pub struct FfxAudioStream {}

#[godot_api]
impl IAudioStream for FfxAudioStream {
    fn instantiate_playback(&self) -> Option<Gd<AudioStreamPlayback>> {
        Some(FfxAudioPlayback::new_gd().upcast())
    }

    fn get_stream_name(&self) -> GString {
        GString::from("ffx-rust mix")
    }

    fn get_length(&self) -> f64 {
        0.0
    }
}

/// One playback of [`FfxAudioStream`]. Godot calls `start` on its main
/// thread before the playback joins the audio server's list, then only
/// its audio thread mixes it; the mix calls no Godot API.
///
/// **Never call this playback from the main thread after `start`** (no
/// `get_playback_position`, `seek`, no method through
/// `get_stream_playback()`): without gdext's `experimental-threads` its
/// borrow cell is not atomic, so a call while the audio thread mixes would
/// race. Everything the main thread needs is in the bridge's queue.
#[derive(GodotClass)]
#[class(base = AudioStreamPlaybackResampled)]
pub struct FfxAudioPlayback {
    base: Base<AudioStreamPlaybackResampled>,
    bridge: Arc<Bridge>,
    playing: bool,
    /// Frames mixed since the start (sound and silence).
    mixed: u64,
}

#[godot_api]
impl IAudioStreamPlaybackResampled for FfxAudioPlayback {
    fn init(base: Base<AudioStreamPlaybackResampled>) -> Self {
        Self {
            base,
            bridge: crate::game::process_bridge(),
            playing: false,
            mixed: 0,
        }
    }

    unsafe fn mix_resampled_rawptr(&mut self, dst: RawPtr<*mut AudioFrame>, frames: i32) -> i32 {
        let Ok(n) = usize::try_from(frames) else {
            return 0;
        };
        let ptr = dst.ptr();
        if n == 0 || ptr.is_null() {
            return 0;
        }
        // SAFETY: Godot hands a buffer of `frames` AudioFrames (repr(C),
        // two f32 each), ours until we return.
        let out = unsafe { std::slice::from_raw_parts_mut(ptr.cast::<f32>(), 2 * n) };
        self.bridge.audio.fill(out);
        self.mixed += n as u64;
        // all of it, silence included: fewer would end the playback
        frames
    }

    fn get_stream_sampling_rate(&self) -> f32 {
        #[allow(clippy::cast_precision_loss)] // 48,000 is exact
        let rate = AUDIO_RATE as f32;
        rate
    }

    fn start(&mut self, _from_pos: f64) {
        self.playing = true;
        self.mixed = 0;
        // clears the resampler's history and mixes its first chunk (silence:
        // the queue waits for its prefill); through base_mut, so the call
        // back into this playback finds it free
        self.base_mut().begin_resample();
    }

    fn stop(&mut self) {
        self.playing = false;
    }

    fn is_playing(&self) -> bool {
        self.playing
    }

    fn get_playback_position(&self) -> f64 {
        #[allow(clippy::cast_precision_loss)] // exact for any real run
        let s = self.mixed as f64 / f64::from(AUDIO_RATE);
        s
    }
}

/// The game's sound, as a node: add it to the tree before the game starts
/// (the output runs only with a player attached). It sets its player up
/// each time it enters the tree and lets it go when it leaves.
#[derive(GodotClass)]
#[class(base = Node)]
pub struct FfxAudioOut {
    base: Base<Node>,
    bridge: Arc<Bridge>,
    player: Option<Gd<AudioStreamPlayer>>,
}

#[godot_api]
impl INode for FfxAudioOut {
    fn init(base: Base<Node>) -> Self {
        Self {
            base,
            bridge: crate::game::process_bridge(),
            player: None,
        }
    }

    fn ready(&mut self) {
        // the game plays on when Godot's tree is paused
        self.base_mut().set_process_mode(ProcessMode::ALWAYS);
        let stream = FfxAudioStream::new_gd();
        let mut player = AudioStreamPlayer::new_alloc();
        player.set_name("Player");
        player.set_stream(&stream);
        player.set_process_mode(ProcessMode::ALWAYS);
        self.base_mut().add_child(&player);
        player.play();
        if !player.is_playing() {
            godot_warn!("ffx: the game's audio stream did not start; the game stays silent");
            player.queue_free();
            return;
        }
        self.player = Some(player);
        self.bridge.audio.attach(true);
        let server = AudioServer::singleton();
        godot_print!(
            "ffx: audio: the game's mix pulled on Godot's audio thread ({} driver, output {} Hz, \
             output latency {:.1} ms)",
            server.get_driver_name(),
            server.get_mix_rate(),
            server.get_output_latency() * 1000.0
        );
    }

    fn exit_tree(&mut self) {
        if let Some(mut p) = self.player.take() {
            self.bridge.audio.attach(false);
            p.stop();
            p.queue_free();
        }
        // set up again if the node comes back
        self.base_mut().request_ready();
    }
}

/// The sound's delay figures for `stats` and `FfxGame.state()`: the
/// queue now and its mean at the device's fills (all our buffering), and
/// Godot's output latency (its driver's buffer), in milliseconds.
#[must_use]
pub fn latency(queued: usize, mean_queued: usize) -> (f64, f64, f64) {
    #[allow(clippy::cast_precision_loss)] // frame counts are small
    let ms = |frames: usize| frames as f64 * 1000.0 / f64::from(AUDIO_RATE);
    let output = AudioServer::singleton().get_output_latency() * 1000.0;
    (ms(queued), ms(mean_queued), output)
}

#[godot_api]
impl FfxAudioOut {
    /// The output's counters: `running` (the game started its output and
    /// this player pulls), `queued` (frames waiting), `mean_queued` (their
    /// mean when the device took frames), `underruns` (the device asked
    /// for more than was queued: heard as gaps), `dropped` (frames, the
    /// queue was full), `pulled` (frames played), and the delay:
    /// `queued_ms`, `mean_queued_ms`, `output_latency_ms` (Godot's driver)
    /// and `latency_ms` (the mean queue and the driver: the mixer to the
    /// speakers).
    #[func]
    fn stats(&self) -> VarDictionary {
        let s = self.bridge.audio.stats();
        let int = |v: u64| i64::try_from(v).unwrap_or(i64::MAX);
        let (queued_ms, mean_ms, output_ms) = latency(s.queued, s.mean_queued);
        vdict! {
            "running" => s.running,
            "queued" => int(s.queued as u64),
            "mean_queued" => int(s.mean_queued as u64),
            "underruns" => int(s.underruns),
            "dropped" => int(s.dropped),
            "pulled" => int(s.pulled),
            "queued_ms" => queued_ms,
            "mean_queued_ms" => mean_ms,
            "output_latency_ms" => output_ms,
            "latency_ms" => mean_ms + output_ms,
        }
    }
}
