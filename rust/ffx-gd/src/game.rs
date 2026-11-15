//! `FfxGame`, the Godot node that runs the game: it installs
//! [`GodotPlatform`], boots ffx-rust from the player's game folder on a
//! thread of its own (the game keeps its own pacing: 30 updates a second
//! on the real clock), feeds it Godot's window events and input, and
//! answers the state the scenes read.
//!
//! One game per process: ffx-rust's platform and parts of its state are
//! the process's.

use std::path::PathBuf;
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use godot::classes::{
    INode, Input, InputEvent, InputEventKey, InputEventMouseButton, InputEventMouseMotion, Node,
};
use godot::global::{JoyAxis, JoyButton, MouseButton};
use godot::prelude::*;

use ffx_host::exec::{Shared, Stop};

use crate::platform::{Bridge, GodotPlatform, PADS, Pad};

/// The bridge of the process's platform (installed by the first start).
static BRIDGE: OnceLock<Arc<Bridge>> = OnceLock::new();

/// The process's classic importer.
fn classic() -> &'static crate::classic::Classic {
    static C: OnceLock<crate::classic::Classic> = OnceLock::new();
    C.get_or_init(crate::classic::Classic::default)
}

/// The process's bridge (made by whichever side comes first: the start,
/// Godot's window events, the audio player).
pub(crate) fn process_bridge() -> Arc<Bridge> {
    Arc::clone(BRIDGE.get_or_init(|| Arc::new(Bridge::default())))
}

/// The game thread's stack (the game's main thread runs on it).
const STACK: usize = 64 << 20;

/// A running (or finished) game.
struct Run {
    bridge: Arc<Bridge>,
    shared: Arc<Mutex<Option<Arc<Shared>>>>,
    outcome: Arc<Mutex<Option<String>>>,
    thread: Option<JoinHandle<()>>,
    started: Instant,
}

impl Run {
    fn shared(&self) -> Option<Arc<Shared>> {
        self.shared.lock().ok().and_then(|s| s.clone())
    }

    fn outcome(&self) -> Option<String> {
        self.outcome.lock().ok().and_then(|o| o.clone())
    }

    fn running(&self) -> bool {
        self.thread.as_ref().is_some_and(|t| !t.is_finished())
    }

    /// Ends the game: `WM_CLOSE` first (the game's own way out of its
    /// loop), then every game thread stopped; waits up to `limit`.
    fn stop(&mut self, limit: Duration) {
        let Some(t) = self.thread.take() else {
            return;
        };
        self.bridge.close();
        // the sound ends with the game (also when it is left running)
        self.bridge.audio.stop();
        let until = Instant::now() + limit;
        let half = Instant::now() + limit / 2;
        let mut asked = false;
        while !t.is_finished() && Instant::now() < until {
            if !asked && Instant::now() >= half {
                if let Some(s) = self.shared() {
                    s.stop_all(Stop::Milestone("stopped by the Godot frontend".into()));
                }
                asked = true;
            }
            std::thread::sleep(Duration::from_millis(5));
        }
        if t.is_finished() {
            let _ = t.join();
        } else {
            godot_warn!("ffx: the game did not end within {limit:?}; left running");
        }
    }
}

/// How a run is started.
struct Boot {
    exe: PathBuf,
    documents: PathBuf,
}

fn run_game(
    boot: &Boot,
    shared: &Mutex<Option<Arc<Shared>>>,
    outcome: &Mutex<Option<String>>,
) -> String {
    let opts = ffx_host::Options {
        documents: Some(boot.documents.clone()),
        gfx: ffx_host::gfx::Config {
            // the platform's device: the null one (Godot draws)
            no_record: true,
            ..ffx_host::gfx::Config::default()
        },
        real_clock: true,
        // Godot shows its own picture; the start-up logos are Iggy's
        // (without Ruffle they would never end)
        skip_opening: true,
        ..ffx_host::Options::default()
    };
    let mut exec = match ffx_host::boot_opts(&boot.exe, ffx_host::ports::table(), &opts) {
        Ok(e) => e,
        Err(e) => return format!("boot: {e}"),
    };
    crate::snapshots::observe(exec.shared());
    if let Ok(mut s) = shared.lock() {
        *s = Some(Arc::clone(exec.shared()));
    }
    let _ = outcome;
    let r = match exec.run_entry() {
        Ok(stop) => format!("{stop}"),
        Err(e) => format!("error: {e}"),
    };
    exec.shared()
        .stop_all(Stop::Milestone("the game's main thread ended".into()));
    exec.join_threads(Duration::from_secs(2));
    r
}

/// The game, as a node: add it to the scene tree, call `start`, read
/// `state` every frame and pass it the input events.
#[derive(GodotClass)]
#[class(base = Node)]
pub struct FfxGame {
    base: Base<Node>,
    run: Option<Run>,
    error: String,
    /// Pads are polled each frame (`process`) unless switched off
    /// (`set_poll_pads`).
    poll_pads: bool,
    /// The requests' replies not yet picked up (`request_results`).
    results: Arc<Mutex<Vec<String>>>,
}

#[godot_api]
impl INode for FfxGame {
    fn init(base: Base<Node>) -> Self {
        Self {
            base,
            run: None,
            error: String::new(),
            poll_pads: true,
            results: Arc::default(),
        }
    }

    fn process(&mut self, _delta: f64) {
        if self.poll_pads
            && let Some(run) = &self.run
        {
            poll_pads(&run.bridge);
        }
    }

    fn exit_tree(&mut self) {
        if let Some(run) = &mut self.run {
            run.stop(Duration::from_secs(6));
        }
    }
}

/// Godot's joypads as `XInput` pads (the first four connected).
fn poll_pads(bridge: &Bridge) {
    let mut input = Input::singleton();
    let pads = input.get_connected_joypads();
    for slot in 0..PADS {
        let pad = pads.get(slot).map(|device| {
            let device = i32::try_from(device).unwrap_or(0);
            let mut p = Pad::default();
            // XINPUT_GAMEPAD bits
            for (button, bit) in [
                (JoyButton::DPAD_UP, 0x0001),
                (JoyButton::DPAD_DOWN, 0x0002),
                (JoyButton::DPAD_LEFT, 0x0004),
                (JoyButton::DPAD_RIGHT, 0x0008),
                (JoyButton::START, 0x0010),
                (JoyButton::BACK, 0x0020),
                (JoyButton::LEFT_STICK, 0x0040),
                (JoyButton::RIGHT_STICK, 0x0080),
                (JoyButton::LEFT_SHOULDER, 0x0100),
                (JoyButton::RIGHT_SHOULDER, 0x0200),
                (JoyButton::A, 0x1000),
                (JoyButton::B, 0x2000),
                (JoyButton::X, 0x4000),
                (JoyButton::Y, 0x8000),
            ] {
                if input.is_joy_button_pressed(device, button) {
                    p.buttons |= bit;
                }
            }
            #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)] // clamped
            let trigger = |a| (input.get_joy_axis(device, a).clamp(0.0, 1.0) * 255.0).round() as u8;
            p.left_trigger = trigger(JoyAxis::TRIGGER_LEFT);
            p.right_trigger = trigger(JoyAxis::TRIGGER_RIGHT);
            #[allow(clippy::cast_possible_truncation)] // clamped
            let stick = |a, flip: f32| {
                (input.get_joy_axis(device, a).clamp(-1.0, 1.0) * flip * 32767.0).round() as i16
            };
            // Godot's y grows downwards, XInput's upwards
            p.thumbs = [
                stick(JoyAxis::LEFT_X, 1.0),
                stick(JoyAxis::LEFT_Y, -1.0),
                stick(JoyAxis::RIGHT_X, 1.0),
                stick(JoyAxis::RIGHT_Y, -1.0),
            ];
            let [weak, strong] = bridge.vibration(slot);
            if weak != 0 || strong != 0 {
                input
                    .start_joy_vibration_ex(
                        device,
                        f32::from(weak) / 65535.0,
                        f32::from(strong) / 65535.0,
                    )
                    .duration(0.1)
                    .done();
            }
            p
        });
        bridge.set_pad(slot, pad);
    }
}

// Godot passes the arguments of a `#[func]` by value; some read the
// process's state, not the node's.
#[allow(clippy::needless_pass_by_value, clippy::unused_self)]
#[godot_api]
impl FfxGame {
    /// Boots the game from `game_dir` (the player's folder with `FFX.exe`
    /// and `data/`) with `documents` as its documents folder (settings and
    /// saves; a sandbox, never the player's own), and runs it on its own
    /// thread. False when it could not start (`last_error` says why).
    #[func]
    fn start(&mut self, game_dir: GString, documents: GString) -> bool {
        self.error.clear();
        if self.run.is_some() {
            return self.fail("the game was started already (one game per process)");
        }
        let game_dir = PathBuf::from(game_dir.to_string());
        let exe = game_dir.join("FFX.exe");
        if !exe.is_file() {
            return self.fail(&format!("no FFX.exe in {}", game_dir.display()));
        }
        let documents = PathBuf::from(documents.to_string());
        if let Err(e) = std::fs::create_dir_all(&documents) {
            return self.fail(&format!("documents folder {}: {e}", documents.display()));
        }
        let bridge = process_bridge();
        let platform = GodotPlatform::new(Arc::clone(&bridge));
        if ffx_host::platform::install(Box::new(platform)).is_err()
            && ffx_host::platform::get().name() != "godot"
        {
            return self.fail("another platform was installed before");
        }
        let shared = Arc::new(Mutex::new(None));
        let outcome = Arc::new(Mutex::new(None));
        let boot = Boot { exe, documents };
        let thread = {
            let (shared, outcome) = (Arc::clone(&shared), Arc::clone(&outcome));
            let bridge = Arc::clone(&bridge);
            std::thread::Builder::new()
                .name("ffx-game".into())
                .stack_size(STACK)
                .spawn(move || {
                    let r = run_game(&boot, &shared, &outcome);
                    // no stale tail, no underruns for the silence after
                    bridge.audio.stop();
                    eprintln!("ffx: the game ended: {r}");
                    if let Ok(mut o) = outcome.lock() {
                        *o = Some(r);
                    }
                })
        };
        match thread {
            Ok(t) => {
                self.run = Some(Run {
                    bridge,
                    shared,
                    outcome,
                    thread: Some(t),
                    started: Instant::now(),
                });
                true
            }
            Err(e) => self.fail(&format!("game thread: {e}")),
        }
    }

    /// Ends the game (`WM_CLOSE`, then a stop), waiting up to
    /// `timeout` seconds.
    #[func]
    fn stop(&mut self, timeout: f64) {
        if let Some(run) = &mut self.run {
            run.stop(Duration::from_secs_f64(timeout.max(0.0)));
        }
    }

    /// Whether the game's thread runs.
    #[func]
    fn is_running(&self) -> bool {
        self.run.as_ref().is_some_and(Run::running)
    }

    /// Why `start` failed.
    #[func]
    fn last_error(&self) -> GString {
        GString::from(self.error.as_str())
    }

    /// The game's state now: `running`, `booted`, `outcome` (why it ended),
    /// `seconds` (wall time since the start), `updates`, `flips`,
    /// `game_time`, `map`, `previous_map`, `entrance`, `story`,
    /// `event_frame`, `keys` (the scan codes held), `focused`,
    /// `message_boxes`, and the sound's `audio_running`, `audio_queued`
    /// (frames), `audio_underruns` (the queue ran dry), `audio_skips`
    /// (Godot's player ran empty: gaps heard), `audio_pulled` (frames
    /// played).
    #[func]
    fn state(&self) -> VarDictionary {
        let Some(run) = &self.run else {
            return vdict! { "running" => false, "booted" => false };
        };
        let mut d = vdict! {
            "running" => run.running(),
            "booted" => run.shared().is_some(),
            "outcome" => run.outcome().unwrap_or_default(),
            "seconds" => run.started.elapsed().as_secs_f64(),
            "focused" => run.bridge.focused(),
        };
        let audio = run.bridge.audio.stats();
        let int = |v: u64| i64::try_from(v).unwrap_or(i64::MAX);
        d.set("audio_running", audio.running);
        d.set("audio_queued", int(audio.queued as u64));
        d.set("audio_underruns", int(audio.underruns));
        d.set("audio_skips", int(audio.skips));
        d.set("audio_pulled", int(audio.pulled));
        if let Some(s) = run.shared() {
            let snap = crate::state::snapshot(&s);
            d.set("updates", i64::from(snap.updates));
            d.set("flips", i64::from(snap.flips));
            d.set("game_time", snap.game_time);
            d.set("map", i64::from(snap.map));
            d.set("previous_map", i64::from(snap.previous_map));
            d.set("entrance", i64::from(snap.entrance));
            d.set("story", i64::from(snap.story));
            d.set("event_frame", i64::from(snap.event_frame));
            if let Some(stop) = s.stopped() {
                d.set("stopped", stop.to_string());
            }
        }
        let keys: PackedInt32Array = run.bridge.keys_down().into_iter().map(i32::from).collect();
        d.set("keys", &keys);
        let boxes: PackedStringArray = run
            .bridge
            .message_boxes()
            .into_iter()
            .map(|(t, m)| GString::from(format!("{t}: {m}").as_str()))
            .collect();
        d.set("message_boxes", &boxes);
        d
    }

    /// The game-state API's newest two snapshots, encoded
    /// (`ffx_state::Snapshot::encode`; C#: `GdFfx.Api.StateApi.Decode`):
    /// `update` (the newest's number, -1 none yet), `previous`, `current`
    /// (`PackedByteArray`, empty when none), `age` (seconds since the
    /// current came), `interval` (seconds between updates, smoothed).
    #[func]
    fn snapshots(&self) -> VarDictionary {
        let s = crate::snapshots::global();
        let crate::snapshots::Encoded {
            previous,
            current,
            age,
            interval,
        } = s.encoded();
        let bytes = |b: Option<Arc<Vec<u8>>>| {
            b.map_or_else(PackedByteArray::new, |b| {
                PackedByteArray::from(b.as_slice())
            })
        };
        vdict! {
            "update" => s.update(),
            "previous" => &bytes(previous),
            "current" => &bytes(current),
            "age" => age,
            "interval" => interval,
        }
    }

    /// Opens the player's archive in `game_dir` for the classic importer,
    /// with `cache` (a folder of gd-ffx's own) as its output. False on
    /// failure (`last_error`).
    #[func]
    fn classic_open(&mut self, game_dir: GString, cache: GString) -> bool {
        match classic().open(
            &PathBuf::from(game_dir.to_string()),
            PathBuf::from(cache.to_string()),
        ) {
            Ok(()) => true,
            Err(e) => self.fail(&format!("classic importer: {e}")),
        }
    }

    /// Imports `name` (`kind`: `map` with a level name, `chr` with a model
    /// name) in the background; `classic_finished` reports it.
    #[func]
    fn classic_request(&mut self, kind: GString, name: GString) -> bool {
        crate::classic::Kind::parse(&kind.to_string())
            .is_some_and(|k| classic().request(k, &name.to_string()))
    }

    /// The imports finished since the last call: `kind`, `name`, `path`
    /// (the `.glb`; empty on failure), `error`.
    #[func]
    fn classic_finished(&mut self) -> Array<VarDictionary> {
        classic()
            .finished()
            .into_iter()
            .map(|d| {
                let (path, error) = match d.result {
                    Ok(p) => (p.display().to_string(), String::new()),
                    Err(e) => (String::new(), e),
                };
                vdict! {
                    "kind" => d.kind.name(),
                    "name" => d.name.as_str(),
                    "path" => path.as_str(),
                    "error" => error.as_str(),
                }
            })
            .collect()
    }

    /// The game's encoded text (a message window's) as text, in
    /// `language`'s character set (`us`, `jp`, `de`...). Empty without the
    /// archive (`classic_open`).
    #[func]
    fn decode_text(&self, text: PackedByteArray, language: GString) -> GString {
        classic()
            .decode(&language.to_string(), text.as_slice())
            .map_or_else(GString::new, |s| GString::from(s.as_str()))
    }

    /// Asks the game to load developer save `n` (1..=110, from the
    /// player's archive) at its next frame boundary. False when the game
    /// has not booted. `request_results` reports the outcome.
    #[func]
    fn request_devsave(&mut self, n: i64) -> bool {
        let Some(shared) = self.run.as_ref().and_then(Run::shared) else {
            return false;
        };
        let results = Arc::clone(&self.results);
        shared.requests.push(
            ffx_host::requests::Request::DevSave(i32::try_from(n).unwrap_or(0)),
            Box::new(move |r| {
                if let Ok(mut v) = results.lock() {
                    v.push(r.unwrap_or_else(|e| format!("err {e}")));
                }
            }),
        );
        true
    }

    /// The requests' replies since the last call (`err ...` on failure).
    #[func]
    fn request_results(&mut self) -> PackedStringArray {
        let v: Vec<String> = self
            .results
            .lock()
            .map(|mut v| std::mem::take(&mut *v))
            .unwrap_or_default();
        v.iter().map(|s| GString::from(s.as_str())).collect()
    }

    /// The newest snapshot's update number (-1 none yet): cheap, to decode
    /// only when it changed.
    #[func]
    fn snapshot_update(&self) -> i64 {
        crate::snapshots::global().update()
    }

    /// A key event (by its physical key, so the game's bindings sit where
    /// they sit on the original's keyboard). True when the game has a code
    /// for the key.
    #[func]
    fn key_input(&mut self, event: Gd<InputEventKey>) -> bool {
        let Some(run) = &self.run else { return false };
        if event.is_echo() {
            return true;
        }
        let Some(scan) = crate::keys::scan_code(event.get_physical_keycode(), event.get_location())
        else {
            return false;
        };
        run.bridge.key(scan, event.is_pressed());
        true
    }

    /// A key by its `DirectInput` scan code (tests, on-screen buttons).
    #[func]
    fn scan_input(&mut self, scan: i64, down: bool) {
        if let (Some(run), Ok(scan)) = (&self.run, u8::try_from(scan)) {
            run.bridge.key(scan, down);
        }
    }

    /// A mouse event (motion, buttons, the wheel).
    #[func]
    fn mouse_input(&mut self, event: Gd<InputEvent>) {
        let Some(run) = &self.run else { return };
        #[allow(clippy::cast_possible_truncation)] // pixels
        if let Ok(m) = event.clone().try_cast::<InputEventMouseMotion>() {
            let (r, p) = (m.get_relative(), m.get_position());
            run.bridge
                .mouse_motion(r.x as i32, r.y as i32, p.x as i32, p.y as i32);
        } else if let Ok(b) = event.try_cast::<InputEventMouseButton>() {
            let down = b.is_pressed();
            match b.get_button_index() {
                MouseButton::LEFT => run.bridge.mouse_button(0, down),
                MouseButton::RIGHT => run.bridge.mouse_button(1, down),
                MouseButton::MIDDLE => run.bridge.mouse_button(2, down),
                MouseButton::XBUTTON1 => run.bridge.mouse_button(3, down),
                MouseButton::XBUTTON2 => run.bridge.mouse_button(4, down),
                MouseButton::WHEEL_UP if down => run.bridge.mouse_wheel(120),
                MouseButton::WHEEL_DOWN if down => run.bridge.mouse_wheel(-120),
                _ => {}
            }
        }
    }

    /// The cursor left the window.
    #[func]
    fn mouse_exited(&mut self) {
        if let Some(run) = &self.run {
            run.bridge.mouse_left();
        }
    }

    /// The window's client size, the screen's size and the window's
    /// position on it (the game gets `WM_SIZE` on a change).
    #[func]
    fn set_window(&mut self, size: Vector2i, screen: Vector2i, origin: Vector2i) {
        let bridge = self.bridge();
        bridge.set_screen((screen.x, screen.y), (origin.x, origin.y));
        bridge.resize(size.x, size.y);
    }

    /// The window got or lost the focus (the game gets `WM_ACTIVATE`; its
    /// keyboard and mouse read nothing without the focus).
    #[func]
    fn set_focus(&mut self, focused: bool) {
        self.bridge().set_focus(focused);
    }

    /// Whether Godot's joypads are polled each frame (default on).
    #[func]
    fn set_poll_pads(&mut self, on: bool) {
        self.poll_pads = on;
    }
}

impl FfxGame {
    /// The process's bridge (made before the start when Godot's window
    /// events come first).
    fn bridge(&self) -> Arc<Bridge> {
        match &self.run {
            Some(run) => Arc::clone(&run.bridge),
            None => process_bridge(),
        }
    }

    fn fail(&mut self, why: &str) -> bool {
        godot_error!("ffx: {why}");
        self.error = why.to_string();
        false
    }
}
