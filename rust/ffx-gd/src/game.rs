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
    /// The game's speed on the real clock at the boot (`set_speed` before
    /// `start`), 1 for the game's own pace.
    speed: f64,
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
        // the debugging speed (`set_speed`, `--speed=`): only when not 1
        speed: ((boot.speed - 1.0).abs() > f64::EPSILON).then_some(boot.speed),
        ..ffx_host::Options::default()
    };
    let mut table = ffx_host::ports::table();
    // the native ESC menu's calls into the game's (docs/architecture.md)
    crate::esc::register(&mut table);
    let mut exec = match ffx_host::boot_opts(&boot.exe, table, &opts) {
        Ok(e) => e,
        Err(e) => return format!("boot: {e}"),
    };
    crate::snapshots::observe(exec.shared());
    // the effects' draws (ffx_host::vfx), drawn by EffectDrawView; FFX_NO_VFX turns it off
    if std::env::var_os("FFX_NO_VFX").is_none() {
        exec.shared().vfx.set_enabled(true);
    }
    // the characters' simulated hair and cloth (ffx_host::cloth), drawn by ClothView;
    // FFX_NO_CLOTH turns it off
    if std::env::var_os("FFX_NO_CLOTH").is_none() {
        exec.shared().cloth.set_enabled(true);
    }
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
    /// The effect frame `effect_draws_held` encoded last: C# copies it from
    /// `effect_draws_address` into a buffer of its own (no new array a
    /// game update on its side: docs/running.md, "Benchmark").
    fx_held: Vec<u8>,
    /// The cloth frame `cloth_held` encoded last, held as `fx_held` is.
    cloth_held: Vec<u8>,
    /// The snapshot pair `snapshots_held` handed out last, kept (and with
    /// it the addresses it gave) until the next call.
    snaps_held: Option<crate::snapshots::Encoded>,
    /// The game's speed (`set_speed`): given to the boot, then to the
    /// running game's clock.
    speed: f64,
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
            fx_held: Vec::new(),
            cloth_held: Vec::new(),
            snaps_held: None,
            speed: 1.0,
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

/// A texture ask from Godot's strings (`face` or `runtime`).
fn texture_ask(kind: &GString, name: &GString, face: i64) -> Option<crate::classic::TextureAsk> {
    use crate::classic::TextureAsk;
    match kind.to_string().as_str() {
        "face" => Some(TextureAsk::Face(
            name.to_string(),
            usize::try_from(face).ok()?,
        )),
        "runtime" => Some(TextureAsk::Runtime(name.to_string())),
        _ => None,
    }
}

/// A texture's pixels as Godot's dictionary (`texture_request`).
fn pixels_dict(id: i64, px: Option<ffx_import::Pixels>) -> VarDictionary {
    let Some(p) = px else {
        return vdict! { "id" => id, "found" => false };
    };
    vdict! {
        "id" => id,
        "found" => true,
        "width" => i64::from(p.width),
        "height" => i64::from(p.height),
        "faces" => i64::try_from(p.faces).unwrap_or(1),
        "rgba" => &PackedByteArray::from(p.rgba.as_slice()),
    }
}

/// A buffer's address in this process and its length, for C# to copy from
/// (`Marshal.Copy`) while it is held; (0, 0) when empty.
fn address_len(b: &[u8]) -> (i64, i64) {
    match (i64::try_from(b.as_ptr().addr()), i64::try_from(b.len())) {
        (Ok(at), Ok(len)) if len > 0 => (at, len),
        _ => (0, 0),
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
        let boot = Boot {
            exe,
            documents,
            speed: self.speed,
        };
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
    /// (frames), `audio_mean_queued` (their mean when Godot's mix took
    /// frames), `audio_underruns` (Godot's mix asked for more than was
    /// queued: gaps heard), `audio_pulled` (frames played), and its delay
    /// in milliseconds: `audio_queued_ms`, `audio_mean_queued_ms`,
    /// `audio_output_latency_ms` (Godot's driver) and `audio_latency_ms`
    /// (the mean queue and the driver: the mixer to the speakers).
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
        d.set("audio_mean_queued", int(audio.mean_queued as u64));
        d.set("audio_pulled", int(audio.pulled));
        let (queued_ms, mean_ms, output_ms) =
            crate::audio::latency(audio.queued, audio.mean_queued);
        d.set("audio_queued_ms", queued_ms);
        d.set("audio_mean_queued_ms", mean_ms);
        d.set("audio_output_latency_ms", output_ms);
        d.set("audio_latency_ms", mean_ms + output_ms);
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
    /// `update` (the newest's number, -1 none yet), `previous_update` (the
    /// previous one's, -1 none), `previous`, `current`
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
        // the previous one's update (-1 none): C# reuses its decoding when it is the one it had
        let previous_update = previous
            .as_ref()
            .and_then(|b| b.get(8..12))
            .and_then(|b| b.try_into().ok())
            .map_or(-1, |b| i64::from(u32::from_le_bytes(b)));
        vdict! {
            "update" => s.update(),
            "previous_update" => previous_update,
            "previous" => &bytes(previous),
            "current" => &bytes(current),
            "age" => age,
            "interval" => interval,
        }
    }

    /// [`snapshots`](Self::snapshots) without copies: the pair stays held
    /// by this node until the next call (main thread only, as every call),
    /// and the dictionary gives each one's address in this process and
    /// length (`current_address`, `current_len`, `previous_address`,
    /// `previous_len`; 0 when none) beside `update`, `previous_update`,
    /// `age` and `interval`. C# copies the bytes into buffers of its own
    /// (`Marshal.Copy`) before its next call here; no Godot array is made.
    #[func]
    fn snapshots_held(&mut self) -> VarDictionary {
        let s = crate::snapshots::global();
        let e = s.encoded();
        let previous_update = e
            .previous
            .as_ref()
            .and_then(|b| b.get(8..12))
            .and_then(|b| b.try_into().ok())
            .map_or(-1, |b| i64::from(u32::from_le_bytes(b)));
        let at = |b: Option<&Arc<Vec<u8>>>| b.map_or((0, 0), |b| address_len(b));
        let (cur_at, cur_len) = at(e.current.as_ref());
        let (prev_at, prev_len) = at(e.previous.as_ref());
        let d = vdict! {
            "update" => s.update(),
            "previous_update" => previous_update,
            "current_address" => cur_at,
            "current_len" => cur_len,
            "previous_address" => prev_at,
            "previous_len" => prev_len,
            "age" => e.age,
            "interval" => e.interval,
        };
        // the addresses above stay valid while these are held
        self.snaps_held = Some(e);
        d
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

    /// Map number `map`'s likely level and its exits, for loading the next
    /// map ahead (docs/architecture.md, "Map transitions"): `ready` (false
    /// while they are read in the background: ask again), `level` (the
    /// level its event most likely shows, empty when not known), `maps`
    /// and `levels` (the maps its event script jumps to with constant
    /// arguments, and theirs).
    #[func]
    fn map_exits(&self, map: i64) -> VarDictionary {
        let x = u16::try_from(map).ok().and_then(|m| classic().map_exits(m));
        let Some(x) = x else {
            return vdict! { "ready" => false };
        };
        let maps: PackedInt32Array = x.exits.iter().map(|&(m, _)| i32::from(m)).collect();
        let levels: PackedStringArray = x
            .exits
            .iter()
            .map(|(_, l)| GString::from(l.as_str()))
            .collect();
        vdict! {
            "ready" => true,
            "level" => x.level.as_str(),
            "maps" => &maps,
            "levels" => &levels,
        }
    }

    /// Calls the ESC menu's Flash callback `EscMenuButtonClick([page,
    /// index])` at the game's next `FFEscMenu_update`, as the page's button
    /// would (`crate::esc`; page 13 is Change parameters). False when the
    /// game has not booted.
    #[func]
    fn esc_button(&mut self, page: i64, index: i64) -> bool {
        if self.run.as_ref().and_then(Run::shared).is_none() {
            return false;
        }
        crate::esc::push(crate::esc::Call::Button {
            page: u32::try_from(page).unwrap_or(0),
            index: u32::try_from(index).unwrap_or(0),
        });
        true
    }

    /// Answers the ESC menu's open pop-up (`EscMenuPopUpConfirmed`): yes
    /// or no.
    #[func]
    fn esc_popup_answer(&mut self, yes: bool) -> bool {
        if self.run.as_ref().and_then(Run::shared).is_none() {
            return false;
        }
        crate::esc::push(crate::esc::Call::PopUp {
            answer: if yes { 1.0 } else { 0.0 },
        });
        true
    }

    /// What the ESC menu's wrapper saw at the game's last
    /// `FFEscMenu_update`: `seen` (it ran), `open`, `parameters` (the
    /// Change parameters entry is enabled, as the game's page would show
    /// it), `popup` (the page's confirmation waits for its answer),
    /// `failed` (a callback could not be called), `done` (calls run so
    /// far), `row` (the row of the last button the game took).
    #[func]
    fn esc_state(&self) -> VarDictionary {
        let (s, done, row) = crate::esc::state();
        let [seen, open, parameters, popup, failed] = crate::esc::bits(s);
        vdict! {
            "seen" => seen,
            "open" => open,
            "parameters" => parameters,
            "popup" => popup,
            "failed" => failed,
            "done" => i64::from(done),
            "row" => i64::from(row.cast_signed()),
        }
    }

    /// The random encounters of the map a random encounter looks up now
    /// (`g_SaveData + 4`, `crate::encounters`): `ready` (false before the
    /// game's table is read, or for a map without one), `map`, and
    /// `groups`, each with its battle map word (`map`), `rate` (0: no
    /// random encounters), the map's `file` and its `level`.
    #[func]
    fn encounter_maps(&self) -> VarDictionary {
        let Some((map, groups)) = self
            .run
            .as_ref()
            .and_then(Run::shared)
            .and_then(|s| crate::encounters::current(&s))
        else {
            return vdict! { "ready" => false };
        };
        let mut list = Array::<VarDictionary>::new();
        for g in &groups {
            list.push(&vdict! {
                "map" => i64::from(g.map),
                "rate" => i64::from(g.rate),
                "file" => g.file.as_str(),
                "level" => crate::encounters::level_of(&g.file).unwrap_or_default().as_str(),
            });
        }
        vdict! { "ready" => true, "map" => i64::from(map), "groups" => &list }
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

    /// The name of the battle command, item or monster ability `id` (the
    /// battle menu's ids) in `language` (`us`, `jp`...); empty without the
    /// archive or such a record.
    #[func]
    fn kernel_name(&self, id: i64, language: GString) -> GString {
        u16::try_from(id)
            .ok()
            .and_then(|id| classic().kernel_name(&language.to_string(), id))
            .map_or_else(GString::new, |s| GString::from(s.as_str()))
    }

    /// `len` bytes of the game's memory at `address` (a developer's look at
    /// structures the game-state API does not carry yet; at most 64 KiB, 0
    /// where not mapped); empty while no game runs.
    #[func]
    fn peek(&self, address: i64, len: i64) -> PackedByteArray {
        let (Ok(at), Ok(n)) = (u32::try_from(address), usize::try_from(len)) else {
            return PackedByteArray::new();
        };
        self.run
            .as_ref()
            .and_then(Run::shared)
            .map_or_else(PackedByteArray::new, |s| {
                PackedByteArray::from(crate::state::peek(&s, at, n).as_slice())
            })
    }

    /// A material texture (its path as the model stores it, e.g. from the
    /// glTF's `extras.ffx_textures`), face `face` (0, or 0..6 for a cube
    /// map), as PNG bytes; empty without the archive or such a face.
    #[func]
    fn classic_texture(&self, path: GString, face: i64) -> PackedByteArray {
        usize::try_from(face)
            .ok()
            .and_then(|f| classic().texture_png(&path.to_string(), f))
            .map_or_else(PackedByteArray::new, |b| {
                PackedByteArray::from(b.as_slice())
            })
    }

    /// A texture by the name the game holds at run time (an effect draw's)
    /// as PNG bytes; empty when it is not in the archive.
    #[func]
    fn runtime_texture(&self, name: GString) -> PackedByteArray {
        classic()
            .runtime_texture_png(&name.to_string())
            .map_or_else(PackedByteArray::new, |b| {
                PackedByteArray::from(b.as_slice())
            })
    }

    /// A texture's pixels: `kind` `face` (a material texture by its path,
    /// face `face`) or `runtime` (by the name the game holds, `face`
    /// unused). Read on a thread of the extension's own and handed over by
    /// `textures_finished` with `id`: Godot's main thread does not wait for
    /// the archive or the block decoding (and Godot's worker threads may
    /// not call into the extension). False when it cannot be asked.
    #[func]
    fn texture_request(&self, id: i64, kind: GString, name: GString, face: i64) -> bool {
        texture_ask(&kind, &name, face).is_some_and(|ask| classic().texture_request(id, ask))
    }

    /// The textures read since the last call (`texture_request`): `id`,
    /// `found`, `width`, `height`, `faces` (6 for a cube map) and `rgba`
    /// (RGBA8 rows, top row first).
    #[func]
    fn textures_finished(&self) -> Array<VarDictionary> {
        classic()
            .textures_finished()
            .into_iter()
            .map(|(id, px)| pixels_dict(id, px))
            .collect()
    }

    /// A texture's pixels now, as `textures_finished`'s entries (id 0).
    #[func]
    fn texture_now(&self, kind: GString, name: GString, face: i64) -> VarDictionary {
        pixels_dict(
            0,
            texture_ask(&kind, &name, face).and_then(|ask| classic().texture_rgba(&ask)),
        )
    }

    /// Whether the material texture at `path` is a cube map.
    #[func]
    fn classic_texture_is_cube(&self, path: GString) -> bool {
        classic().texture_is_cube(&path.to_string())
    }

    /// Which of `names` the material effect variant `effect` (a glTF
    /// material's `extras.ffx_effect`) switches on (`MASK_DEPTH`,
    /// `ADDITIVE_BLENDING`...); empty without the archive.
    #[func]
    fn classic_effect_switches(
        &self,
        effect: GString,
        names: PackedStringArray,
    ) -> PackedStringArray {
        let names: Vec<String> = names.as_slice().iter().map(GString::to_string).collect();
        let names: Vec<&str> = names.iter().map(String::as_str).collect();
        classic()
            .effect_switches(&effect.to_string(), &names)
            .iter()
            .map(GString::from)
            .collect()
    }

    /// The effects' draws of the frame that drew update `update` (the
    /// snapshot's `update`), else the newest, encoded as `ffx_host::vfx`'s
    /// `Frame::encode` (docs/state-api.md "Effect draws" in ffx-rust); empty
    /// without a game or a captured frame.
    #[func]
    fn effect_draws(&self, update: i64) -> PackedByteArray {
        let Some(shared) = self.run.as_ref().and_then(Run::shared) else {
            return PackedByteArray::new();
        };
        let frame = u32::try_from(update)
            .ok()
            .and_then(|u| shared.vfx.frame(u))
            .or_else(|| shared.vfx.latest());
        frame.map_or_else(PackedByteArray::new, |f| {
            PackedByteArray::from(f.encode().as_slice())
        })
    }

    /// [`effect_draws`](Self::effect_draws) without a Godot array: the frame
    /// is encoded into a buffer this node holds until the next call (main
    /// thread only), whose length this returns (0: none);
    /// `effect_draws_address` gives its address. C# copies it into a buffer
    /// of its own (a new 344 KiB array an update at Besaid made gen2
    /// collections: docs/running.md, "Benchmark").
    #[func]
    fn effect_draws_held(&mut self, update: i64) -> i64 {
        self.fx_held.clear();
        if let Some(shared) = self.run.as_ref().and_then(Run::shared) {
            let frame = u32::try_from(update)
                .ok()
                .and_then(|u| shared.vfx.frame(u))
                .or_else(|| shared.vfx.latest());
            if let Some(f) = frame {
                self.fx_held = f.encode();
            }
        }
        address_len(&self.fx_held).1
    }

    /// The address of the frame `effect_draws_held` encoded last (valid
    /// until the next `effect_draws_held`; 0 when it is empty).
    #[func]
    fn effect_draws_address(&self) -> i64 {
        address_len(&self.fx_held).0
    }

    /// The characters' cloth of the frame that drew update `update` (the
    /// snapshot's `update`; no other: a frame of another update would put
    /// the cloth where the skeleton is not), encoded as `ffx_host::cloth`'s
    /// `Frame::encode` (`CLTH`, docs/state-api.md "Cloth" in ffx-rust) into
    /// a buffer this node holds until the next call (main thread only), as
    /// `effect_draws_held`; returns its length (0: no such frame, or no
    /// game). `cloth_address` gives its address.
    #[func]
    fn cloth_held(&mut self, update: i64) -> i64 {
        self.cloth_held.clear();
        if let Some(shared) = self.run.as_ref().and_then(Run::shared)
            && let Some(f) = u32::try_from(update)
                .ok()
                .and_then(|u| shared.cloth.frame(u))
        {
            self.cloth_held = f.encode();
        }
        address_len(&self.cloth_held).1
    }

    /// The address of the frame `cloth_held` encoded last (valid until the
    /// next `cloth_held`; 0 when it is empty).
    #[func]
    fn cloth_address(&self) -> i64 {
        address_len(&self.cloth_held).0
    }

    /// Sphere grid node kind `kind`'s name in `language`; empty without the
    /// archive or such a record.
    #[func]
    fn grid_node_name(&self, kind: i64, language: GString) -> GString {
        u16::try_from(kind)
            .ok()
            .and_then(|k| classic().grid_node_name(&language.to_string(), k))
            .map_or_else(GString::new, |s| GString::from(s.as_str()))
    }

    /// The save list's place name `index` (a save header's location) in
    /// `language`; empty without the archive.
    #[func]
    fn place_name(&self, index: i64, language: GString) -> GString {
        u16::try_from(index)
            .ok()
            .and_then(|i| classic().place_name(&language.to_string(), i))
            .map_or_else(GString::new, |s| GString::from(s.as_str()))
    }

    /// What names gear `id` (a battle spoil's or an inventory slot's id,
    /// `MsGetSaveWeapon`'s), read from the game now: its `w_name` record,
    /// plus its character `<< 16`, plus `1 << 24` with the config's
    /// hiragana; -1 without a game, for id `0xff` or a record not in use.
    /// `weapon_name` names it.
    #[func]
    fn gear_record(&self, id: i64) -> i64 {
        let (Some(shared), Ok(id)) = (self.run.as_ref().and_then(Run::shared), u16::try_from(id))
        else {
            return -1;
        };
        crate::state::gear(&shared, id).map_or(-1, |(name, chr, kana)| {
            i64::from(name) | i64::from(chr) << 16 | i64::from(kana) << 24
        })
    }

    /// The name of a piece of gear (`gear_record`'s value) in `language`
    /// (`MsWeaponName`: the kernel's `w_name`); empty without the archive
    /// or such a record.
    #[func]
    fn weapon_name(&self, record: i64, language: GString) -> GString {
        let (Ok(name), Ok(chr)) = (
            u16::try_from(record & 0xffff),
            u8::try_from(record >> 16 & 0xff),
        ) else {
            return GString::new();
        };
        if record < 0 {
            return GString::new();
        }
        classic()
            .weapon_name(&language.to_string(), name, chr, record >> 24 & 1 != 0)
            .map_or_else(GString::new, |s| GString::from(s.as_str()))
    }

    /// Line `index` of the remaster's localisation kit in `language`
    /// (`LocalizationManager_getText`; the config's PC row, the save
    /// descriptions), decoded; empty without the archive.
    #[func]
    fn loc_text(&self, index: i64, language: GString) -> GString {
        u32::try_from(index)
            .ok()
            .and_then(|i| classic().loc_text(&language.to_string(), i))
            .map_or_else(GString::new, |s| GString::from(s.as_str()))
    }

    /// Line `index` of the localisation kit in `language` as UTF-8 (the
    /// Flash pages' and tips' lines: the Esc menu's entries, the boosters'
    /// tips); empty without the archive.
    #[func]
    fn loc_text_utf8(&self, index: i64, language: GString) -> GString {
        u32::try_from(index)
            .ok()
            .and_then(|i| classic().loc_text_utf8(&language.to_string(), i))
            .map_or_else(GString::new, |s| GString::from(s.as_str()))
    }

    /// Asks the game to start a battle (`field` the battle field's record,
    /// its `group` and `formation`) as a random encounter would; refused
    /// while one runs. `request_results` reports it.
    #[func]
    fn request_battle(&mut self, field: i64, group: i64, formation: i64) -> bool {
        let Some(shared) = self.run.as_ref().and_then(Run::shared) else {
            return false;
        };
        let results = Arc::clone(&self.results);
        shared.requests.push(
            ffx_host::requests::Request::Battle {
                field: u16::try_from(field).unwrap_or(0),
                group: u8::try_from(group).unwrap_or(0),
                formation: u8::try_from(formation).unwrap_or(0),
            },
            Box::new(move |r| {
                if let Ok(mut v) = results.lock() {
                    v.push(r.unwrap_or_else(|e| format!("err {e}")));
                }
            }),
        );
        true
    }

    /// Writes the battle party (`g_SaveInParty`, `0x011307e8`, the 8 bytes
    /// `MsGetSaveInParty` reads: a party index each, 0xff an empty place)
    /// before a battle is asked for, as ffx-rust's `cloth-probe --party`
    /// does: for the checks (`--party=HEX` with `--battle`), to bring a
    /// character the save keeps in the back line into the battle. False when
    /// the game has not booted or `party` is not 8 bytes.
    #[func]
    fn set_battle_party(&mut self, party: PackedByteArray) -> bool {
        const SAVE_IN_PARTY: u32 = 0x0113_07e8;
        let Some(shared) = self.run.as_ref().and_then(Run::shared) else {
            return false;
        };
        if party.len() != 8 {
            return false;
        }
        shared.arena().write_bytes(SAVE_IN_PARTY, party.as_slice());
        true
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

    /// Sets the game's speed on the real clock, for debugging (ffx-host's
    /// `Clock::set_speed`, docs/host.md "Game speed" in ffx-rust): game
    /// time and the updates run `speed` times as fast (clamped to 0.25 ..
    /// 8; not a number: 1). Before `start` it is passed to the boot
    /// (`Options::speed`); while the game runs it changes at once, game
    /// time going on from where it is. Returns the speed now in use.
    #[func]
    fn set_speed(&mut self, speed: f64) -> f64 {
        let range = ffx_host::clock::SPEEDS;
        self.speed = if speed.is_nan() {
            1.0
        } else {
            speed.clamp(*range.start(), *range.end())
        };
        if let Some(shared) = self.run.as_ref().and_then(Run::shared) {
            shared.clock.set_speed(self.speed);
            return shared.clock.speed();
        }
        self.speed
    }

    /// The game's speed (`set_speed`; 1 unless set): the running game's
    /// clock's, else the one the next `start` boots with.
    #[func]
    fn speed(&self) -> f64 {
        self.run
            .as_ref()
            .and_then(Run::shared)
            .map_or(self.speed, |s| s.clock.speed())
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

#[cfg(test)]
mod tests {
    use super::address_len;

    #[test]
    fn a_held_buffer_by_address_and_length() {
        assert_eq!(address_len(&[]), (0, 0));
        let b = vec![1u8, 2, 3];
        let (at, len) = address_len(&b);
        assert_eq!(len, 3);
        assert_eq!(usize::try_from(at).unwrap(), b.as_ptr().addr());
    }
}
