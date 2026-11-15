//! [`GodotPlatform`]: ffx-rust's platform seam (`ffx_host::platform::Platform`,
//! ffx-rust's docs/host.md "The platform seam") answered from Godot.
//!
//! The game runs on threads of its own and never calls into Godot; Godot's
//! main thread never calls into the game. The two meet in [`Bridge`], a few
//! mutexes and queues: Godot's side ([`crate::game::FfxGame`]) writes the
//! window's size and focus, the keys, the mouse and the pads into it, the
//! game's side reads them when it pumps its messages and polls its devices.
//!
//! - **Window.** `windowed` is true: the game's window procedure gets its
//!   `WM_CREATE` and the messages Godot's window events become (`WM_SIZE`,
//!   `WM_ACTIVATE`, `WM_CLOSE`, queued and drained by `take_sent`).
//!   `WM_CLOSE` ends the run. The game's own posted messages go through
//!   a queue here.
//! - **Frames.** The null graphics device: the game's D3D11 calls are
//!   answered and dropped; Godot draws from the game's state instead
//!   (docs/architecture.md).
//! - **Input.** The system keyboard (`DirectInput` scan codes, 256 bytes)
//!   and mouse (`DIMOUSESTATE2`) as `DirectInput` devices, Godot's joypads as
//!   `XInput` pads (listed as game controllers so that the game adds them,
//!   as `FFX.exe` does with an `XInput` pad). Without the window's focus the
//!   keyboard and mouse fail with `DIERR_INPUTLOST`, as at the game's
//!   foreground cooperative level.
//! - **Sound.** The game's own mix (ffx-rust's FMOD mixer: 48,000 Hz
//!   interleaved stereo f32, blocks of 1,600 frames) goes into
//!   [`AudioQueue`]; Godot's side ([`crate::audio::FfxAudioOut`], a
//!   custom `AudioStreamPlayback`) takes from it on Godot's audio thread
//!   as the device asks for frames ([`AudioQueue::fill`]). The output runs
//!   only while that player is attached; else the game's feeder follows
//!   the wall clock and the mix goes nowhere.
//! - **Steam** is absent; the **documents folder** is the sandbox the
//!   frontend passes to the boot, never the player's own.

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use ffx_host::platform::{
    DeviceCall, DeviceInfo, Message, Platform, Point, Property, Rect, WindowRequest,
};

/// The handle of the game's window (any nonzero word the game keeps).
pub const WINDOW: u32 = 0x0002_0020;

/// `WM_SIZE`.
pub const WM_SIZE: u32 = 0x0005;
/// `WM_ACTIVATE`.
pub const WM_ACTIVATE: u32 = 0x0006;
/// `WM_CLOSE`.
pub const WM_CLOSE: u32 = 0x0010;
/// `WM_QUIT`.
const WM_QUIT: u32 = 0x0012;

/// `GUID_SysKeyboard`.
pub const SYS_KEYBOARD: u128 = 0x6f1d_2b61_d5a0_11cf_bfc7_4445_5354_0000;
/// `GUID_SysMouse`.
pub const SYS_MOUSE: u128 = 0x6f1d_2b60_d5a0_11cf_bfc7_4445_5354_0000;
/// The pads' instance GUIDs: this plus the pad's index in the low byte of
/// `Data1` (made up, as a driver's are).
const PAD_INSTANCE: u128 = 0x9d0f_7c00_6a71_11f1_8000_4744_4646_5800;
/// The `XInput` vendor and product the pads claim (Microsoft's Xbox 360
/// pad), so the game's `Input_IsXInputDevice` takes them as `XInput`.
pub const PAD_VENDOR: u32 = 0x045e;
/// See [`PAD_VENDOR`].
pub const PAD_PRODUCT: u32 = 0x028e;
/// `XInput`'s pads.
pub const PADS: usize = 4;

#[allow(clippy::cast_possible_wrap)] // HRESULT bits
const fn hr(v: u32) -> i32 {
    v as i32
}
const DI_OK: i32 = 0;
const DI_NOEFFECT: i32 = 1;
const DIERR_INPUTLOST: i32 = hr(0x8007_001e);
const DIERR_NOTACQUIRED: i32 = hr(0x8007_000c);
const DIERR_DEVICENOTREG: i32 = hr(0x8004_0154);
const DIERR_UNSUPPORTED: i32 = hr(0x8000_4001);
const DIERR_OTHERAPPHASPRIO: i32 = hr(0x8007_0005);

/// One of Godot's joypads as `XInput` sees it.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Pad {
    /// `XINPUT_GAMEPAD` button bits.
    pub buttons: u16,
    /// Triggers, 0..=255.
    pub left_trigger: u8,
    /// See `left_trigger`.
    pub right_trigger: u8,
    /// Sticks, -32768..=32767, up positive.
    pub thumbs: [i16; 4],
}

impl Pad {
    /// `XINPUT_STATE` with `packet`.
    #[must_use]
    pub fn state(&self, packet: u32) -> [u8; 16] {
        let mut s = [0u8; 16];
        s[0..4].copy_from_slice(&packet.to_le_bytes());
        s[4..6].copy_from_slice(&self.buttons.to_le_bytes());
        s[6] = self.left_trigger;
        s[7] = self.right_trigger;
        for (i, t) in self.thumbs.iter().enumerate() {
            s[8 + 2 * i..10 + 2 * i].copy_from_slice(&t.to_le_bytes());
        }
        s
    }
}

/// The mouse between two polls: motion and wheel since the last, buttons
/// now.
#[derive(Debug, Clone, Copy, Default)]
pub struct Mouse {
    /// Relative motion since the last poll.
    pub dx: i32,
    /// See `dx`.
    pub dy: i32,
    /// Wheel since the last poll (120 a notch).
    pub dz: i32,
    /// Buttons (left, right, middle, then the extra ones).
    pub buttons: [bool; 8],
}

/// What a `DirectInput` device of the game's is behind.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Device {
    Keyboard,
    Mouse,
    Pad(usize),
}

/// A pad as last polled and its packet number.
#[derive(Debug, Clone, Copy, Default)]
struct PadSlot {
    pad: Option<Pad>,
    packet: u32,
    vibration: [u16; 2],
}

/// The mix's sample rate (FMOD's, ffx-rust's mixer).
pub const AUDIO_RATE: u32 = 48_000;
/// Frames queued before the output (re)starts after an underrun: three of
/// the mixer's blocks (100 ms), as ffx-rust's WASAPI output.
pub const AUDIO_PREFILL: usize = 4_800;
/// Frames the queue keeps at most (half a second); older ones are dropped.
pub const AUDIO_MAX_QUEUE: usize = 24_000;

/// The samples between the game's mixer and Godot's player.
#[derive(Debug, Default)]
struct AudioRing {
    /// Interleaved stereo, oldest first.
    samples: VecDeque<f32>,
    /// Godot's side has a player pulling (kept under the lock, so a push
    /// never lands after the player left).
    attached: bool,
    /// Handing frames to the player (false at the start and after an
    /// underrun until [`AUDIO_PREFILL`] frames are queued).
    playing: bool,
    /// The queue's time average while playing, since the player attached:
    /// frames times seconds, and the seconds (by Little's law, the mean
    /// delay of a frame is the mean queue over the rate, however bursty the
    /// device's asks are).
    area: f64,
    span: f64,
    /// When the queue last changed (push or fill).
    since: Option<Instant>,
}

impl AudioRing {
    fn clear(&mut self) {
        self.samples.clear();
        self.playing = false;
    }

    /// The queue is about to change at `now`: what it held since the last
    /// change goes into the time average (while playing).
    fn tick(&mut self, now: Instant) {
        if let Some(t) = self.since
            && self.playing
        {
            let dt = now.saturating_duration_since(t).as_secs_f64();
            #[allow(clippy::cast_precision_loss)] // frame counts are small
            let q = (self.samples.len() / 2) as f64;
            self.area += q * dt;
            self.span += dt;
        }
        self.since = Some(now);
    }

    /// The queue's time average in frames (0 before any playing).
    fn mean(&self) -> usize {
        if self.span > 0.0 {
            #[allow(clippy::cast_possible_truncation, clippy::cast_sign_loss)] // >= 0, small
            let m = (self.area / self.span).round() as usize;
            m
        } else {
            0
        }
    }
}

/// The game's mix on its way to Godot's audio, kept as ffx-rust's WASAPI
/// output keeps it (its `rust_audio_out.rs`): the game's feeder pushes
/// blocks and never waits; the player takes frames on Godot's audio
/// thread exactly as the device asks for them ([`AudioQueue::fill`]), so
/// the queue is all the buffering on our side and `audio_queued` reports
/// all of it to the feeder. At most [`AUDIO_MAX_QUEUE`] frames are kept,
/// the oldest dropped; a fill hands out silence until [`AUDIO_PREFILL`]
/// frames are there, and counts an underrun when the device asked for
/// more than there was while playing.
#[derive(Debug, Default)]
pub struct AudioQueue {
    ring: Mutex<AudioRing>,
    /// The game asked for the output (`audio_start`) and has not ended.
    started: AtomicBool,
    underruns: AtomicU64,
    /// Frames dropped (the queue was full).
    dropped: AtomicU64,
    /// Frames handed to the player.
    pulled: AtomicU64,
}

/// The output's counters (diagnostics).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct AudioStats {
    /// The output runs (started and a player attached).
    pub running: bool,
    /// Frames queued now.
    pub queued: usize,
    /// Underruns so far (the queue ran dry).
    pub underruns: u64,
    /// Frames dropped so far (the queue was full).
    pub dropped: u64,
    /// Frames handed to the player so far.
    pub pulled: u64,
    /// The queue's time average while playing, since the player attached
    /// (0 before): the mean delay between the mixer and Godot's mix, in
    /// frames.
    pub mean_queued: usize,
}

impl AudioQueue {
    /// The game asked for the output.
    pub fn start(&self) {
        self.started.store(true, Ordering::Release);
    }

    /// The game ended: nothing more comes and what is queued goes (no stale
    /// tail for a later player, no underrun for the silence after).
    pub fn stop(&self) {
        self.started.store(false, Ordering::Release);
        lock(&self.ring).clear();
    }

    /// A player is attached (true) or gone (false). Either way the queue
    /// starts empty (and its mean anew).
    pub fn attach(&self, on: bool) {
        let mut r = lock(&self.ring);
        r.attached = on;
        r.clear();
        r.area = 0.0;
        r.span = 0.0;
        r.since = None;
    }

    /// Whether the output runs: the game started it and a player pulls.
    #[must_use]
    pub fn running(&self) -> bool {
        self.started.load(Ordering::Acquire) && lock(&self.ring).attached
    }

    /// Frames queued and not pulled yet.
    #[must_use]
    pub fn queued(&self) -> usize {
        lock(&self.ring).samples.len() / 2
    }

    /// Underruns so far.
    #[must_use]
    pub fn underruns(&self) -> u64 {
        self.underruns.load(Ordering::Relaxed)
    }

    /// Appends one mixed block (interleaved stereo); dropped while the
    /// output does not run. Keeps at most [`AUDIO_MAX_QUEUE`] frames.
    pub fn push(&self, block: &[f32]) {
        self.push_at(block, Instant::now());
    }

    fn push_at(&self, block: &[f32], now: Instant) {
        let mut r = lock(&self.ring);
        if !r.attached || !self.started.load(Ordering::Acquire) {
            return;
        }
        r.tick(now);
        r.samples.extend(block.iter().copied());
        // whole frames only, so left and right stay in place
        let over = r.samples.len().saturating_sub(2 * AUDIO_MAX_QUEUE) & !1;
        if over > 0 {
            r.samples.drain(..over);
            self.dropped.fetch_add(over as u64 / 2, Ordering::Relaxed);
        }
    }

    /// Fills `out` (interleaved stereo, whole frames) for the device, as
    /// Godot's audio thread asks: the queued frames, oldest first, and
    /// silence for the rest; returns the frames of sound. Silence until
    /// [`AUDIO_PREFILL`] frames are queued; an underrun when the device
    /// asked for more than there was while playing (then silence again
    /// until the prefill).
    pub fn fill(&self, out: &mut [f32]) -> usize {
        self.fill_at(out, Instant::now())
    }

    fn fill_at(&self, out: &mut [f32], now: Instant) -> usize {
        let frames = out.len() / 2;
        let mut r = lock(&self.ring);
        r.tick(now);
        let queued = r.samples.len() / 2;
        if !r.playing && queued >= AUDIO_PREFILL {
            r.playing = true;
        }
        let n = if r.playing { queued.min(frames) } else { 0 };
        if r.playing && n < frames {
            self.underruns.fetch_add(1, Ordering::Relaxed);
            r.playing = false;
        }
        for (d, s) in out.iter_mut().zip(r.samples.drain(..2 * n)) {
            *d = s;
        }
        drop(r);
        out[2 * n..].fill(0.0);
        self.pulled.fetch_add(n as u64, Ordering::Relaxed);
        n
    }

    /// The counters now.
    #[must_use]
    pub fn stats(&self) -> AudioStats {
        AudioStats {
            running: self.running(),
            queued: self.queued(),
            underruns: self.underruns(),
            dropped: self.dropped.load(Ordering::Relaxed),
            pulled: self.pulled.load(Ordering::Relaxed),
            mean_queued: lock(&self.ring).mean(),
        }
    }
}

/// What Godot's side and the game's side share.
#[derive(Debug)]
pub struct Bridge {
    /// Messages "sent" to the game's window: (window, message, wParam,
    /// lParam), drained by `take_sent`.
    sent: Mutex<VecDeque<[u32; 4]>>,
    /// Messages the game posted to itself.
    posted: Mutex<VecDeque<Message>>,
    /// The window's client size.
    size: Mutex<(i32, i32)>,
    /// The screen's size.
    screen: Mutex<(i32, i32)>,
    /// The window's top left corner on the screen.
    origin: Mutex<(i32, i32)>,
    /// Whether the window has the focus (the input devices read only then).
    focused: AtomicBool,
    /// The cursor in client coordinates (`None`: far outside, so no menu
    /// sees a hover; the headless platform's position).
    cursor: Mutex<Option<(i32, i32)>>,
    /// `ShowCursor`'s display count.
    cursor_count: AtomicI32,
    /// The clip rectangle.
    clip: Mutex<Option<Rect>>,
    /// `DirectInput`'s 256 scan-code bytes (0x80: down).
    keys: Mutex<[u8; 256]>,
    mouse: Mutex<Mouse>,
    pads: Mutex<[PadSlot; PADS]>,
    /// The game's devices by the layer's handle.
    devices: Mutex<HashMap<u32, Device>>,
    /// `SetWindowLongA` values by index.
    longs: Mutex<HashMap<i32, i32>>,
    /// The window was made (`create_window`).
    window_made: AtomicBool,
    /// The program's message boxes (title, text), newest last.
    boxes: Mutex<Vec<(String, String)>>,
    /// The game's mix for Godot's player.
    pub audio: AudioQueue,
}

impl Default for Bridge {
    fn default() -> Self {
        Self {
            sent: Mutex::default(),
            posted: Mutex::default(),
            size: Mutex::new((1920, 1080)),
            screen: Mutex::new((1920, 1080)),
            origin: Mutex::new((0, 0)),
            focused: AtomicBool::new(true),
            cursor: Mutex::new(None),
            cursor_count: AtomicI32::new(0),
            clip: Mutex::new(None),
            keys: Mutex::new([0; 256]),
            mouse: Mutex::default(),
            pads: Mutex::default(),
            devices: Mutex::default(),
            longs: Mutex::default(),
            window_made: AtomicBool::new(false),
            boxes: Mutex::default(),
            audio: AudioQueue::default(),
        }
    }
}

fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
}

#[allow(clippy::cast_sign_loss)] // a Win32 word's bits
fn word(v: i32) -> u32 {
    v as u32
}

impl Bridge {
    // ---- Godot's side ------------------------------------------------------

    /// Sends a message to the game's window (drained at its next message
    /// pump).
    pub fn send(&self, message: u32, wparam: u32, lparam: u32) {
        lock(&self.sent).push_back([WINDOW, message, wparam, lparam]);
    }

    /// The window's client area is `w` x `h` (sends `WM_SIZE` once the
    /// window exists).
    pub fn resize(&self, w: i32, h: i32) {
        let (w, h) = (w.max(1), h.max(1));
        let changed = {
            let mut s = lock(&self.size);
            let changed = *s != (w, h);
            *s = (w, h);
            changed
        };
        if changed && self.window_made.load(Ordering::Acquire) {
            self.send(WM_SIZE, 0, (word(h) & 0xffff) << 16 | (word(w) & 0xffff));
        }
    }

    /// The screen's size and the window's position on it.
    pub fn set_screen(&self, screen: (i32, i32), origin: (i32, i32)) {
        *lock(&self.screen) = screen;
        *lock(&self.origin) = origin;
    }

    /// The window got or lost the focus (sends `WM_ACTIVATE`).
    pub fn set_focus(&self, focused: bool) {
        let was = self.focused.swap(focused, Ordering::AcqRel);
        if was != focused {
            if !focused {
                *lock(&self.keys) = [0; 256];
                *lock(&self.mouse) = Mouse::default();
            }
            if self.window_made.load(Ordering::Acquire) {
                self.send(WM_ACTIVATE, u32::from(focused), 0);
            }
        }
    }

    /// Whether the window has the focus.
    #[must_use]
    pub fn focused(&self) -> bool {
        self.focused.load(Ordering::Acquire)
    }

    /// Asks the game to end (`WM_CLOSE` at its next message pump).
    pub fn close(&self) {
        self.send(WM_CLOSE, 0, 0);
    }

    /// A key went down or up (a `DirectInput` scan code).
    pub fn key(&self, scan: u8, down: bool) {
        lock(&self.keys)[usize::from(scan)] = if down { 0x80 } else { 0 };
    }

    /// The keys down now (scan codes).
    #[must_use]
    pub fn keys_down(&self) -> Vec<u8> {
        let keys = lock(&self.keys);
        (0..=255u8)
            .filter(|&k| keys[usize::from(k)] & 0x80 != 0)
            .collect()
    }

    /// The mouse moved by (`dx`, `dy`) to (`x`, `y`) in the window.
    pub fn mouse_motion(&self, dx: i32, dy: i32, x: i32, y: i32) {
        let mut m = lock(&self.mouse);
        m.dx += dx;
        m.dy += dy;
        *lock(&self.cursor) = Some((x, y));
    }

    /// A mouse button (0 left, 1 right, 2 middle, 3.. extra).
    pub fn mouse_button(&self, i: usize, down: bool) {
        if let Some(b) = lock(&self.mouse).buttons.get_mut(i) {
            *b = down;
        }
    }

    /// The wheel turned (120 a notch, positive away from the player).
    pub fn mouse_wheel(&self, dz: i32) {
        lock(&self.mouse).dz += dz;
    }

    /// The cursor left the window.
    pub fn mouse_left(&self) {
        *lock(&self.cursor) = None;
    }

    /// Pad `i`'s state now (`None`: not connected).
    pub fn set_pad(&self, i: usize, pad: Option<Pad>) {
        if let Some(slot) = lock(&self.pads).get_mut(i)
            && slot.pad != pad
        {
            slot.pad = pad;
            slot.packet = slot.packet.wrapping_add(1);
        }
    }

    /// Pad `i`'s vibration the game asked for (left, right motor).
    #[must_use]
    pub fn vibration(&self, i: usize) -> [u16; 2] {
        lock(&self.pads).get(i).map_or([0; 2], |s| s.vibration)
    }

    /// The program's message boxes so far.
    #[must_use]
    pub fn message_boxes(&self) -> Vec<(String, String)> {
        lock(&self.boxes).clone()
    }

    // ---- the game's side -----------------------------------------------------

    fn device(&self, handle: u32) -> Option<Device> {
        lock(&self.devices).get(&handle).copied()
    }

    fn connected_pads(&self) -> Vec<usize> {
        lock(&self.pads)
            .iter()
            .enumerate()
            .filter(|(_, s)| s.pad.is_some())
            .map(|(i, _)| i)
            .collect()
    }

    fn screen_rect(&self) -> Rect {
        let (w, h) = *lock(&self.screen);
        Rect {
            left: 0,
            top: 0,
            right: w,
            bottom: h,
        }
    }
}

/// Writes `s` as a NUL-terminated ANSI string into `b` at `at` (at most
/// `max` bytes with the NUL).
fn put_str(b: &mut [u8], at: usize, max: usize, s: &str) {
    let bytes = s.as_bytes();
    let n = bytes.len().min(max - 1);
    if b.len() > at + n {
        b[at..at + n].copy_from_slice(&bytes[..n]);
        b[at + n] = 0;
    }
}

/// A GUID as the game's 16 bytes (`Data1`..`Data3` little-endian, `Data4`
/// as is): `ffx_host::com::read_guid`'s inverse.
fn guid_bytes(g: u128) -> [u8; 16] {
    #[allow(clippy::cast_possible_truncation)] // the fields' bits
    let (d1, d2, d3, d4) = (
        (g >> 96) as u32,
        (g >> 80) as u16,
        (g >> 64) as u16,
        g as u64,
    );
    let mut b = [0u8; 16];
    b[0..4].copy_from_slice(&d1.to_le_bytes());
    b[4..6].copy_from_slice(&d2.to_le_bytes());
    b[6..8].copy_from_slice(&d3.to_le_bytes());
    b[8..16].copy_from_slice(&d4.to_be_bytes());
    b
}

/// Pad `i`'s instance GUID.
fn pad_instance(i: usize) -> u128 {
    PAD_INSTANCE | ((i as u128) << 96)
}

/// The product GUID `DirectInput` gives a HID device: `Data1` vendor | product
/// << 16, then `0000-0000-0000-504944564944` ("PIDVID").
fn pad_product() -> u128 {
    (u128::from(PAD_VENDOR | PAD_PRODUCT << 16) << 96) | 0x5049_4456_4944
}

/// `DI8DEVTYPE_GAMEPAD` with `DI8DEVTYPEGAMEPAD_STANDARD`, a HID device.
const DEVTYPE_GAMEPAD: u32 = 0x0001_0215;
/// `DI8DEVTYPE_KEYBOARD` (PC enhanced).
const DEVTYPE_KEYBOARD: u32 = 0x0000_0413;
/// `DI8DEVTYPE_MOUSE` (unknown subtype).
const DEVTYPE_MOUSE: u32 = 0x0000_0112;

/// A device's `DIDEVICEINSTANCEA` (0x244 bytes).
fn instance(guid: u128, product: u128, devtype: u32, name: &str) -> Vec<u8> {
    let mut b = vec![0u8; ffx_host::input::DEVICE_INSTANCE];
    b[0..4].copy_from_slice(&0x244u32.to_le_bytes());
    b[4..0x14].copy_from_slice(&guid_bytes(guid));
    b[0x14..0x24].copy_from_slice(&guid_bytes(product));
    b[0x24..0x28].copy_from_slice(&devtype.to_le_bytes());
    put_str(&mut b, 0x28, 260, name);
    put_str(&mut b, 0x12c, 260, name);
    if devtype == DEVTYPE_GAMEPAD {
        // HID usage page 1 (generic desktop), usage 5 (game pad)
        b[0x240..0x242].copy_from_slice(&1u16.to_le_bytes());
        b[0x242..0x244].copy_from_slice(&5u16.to_le_bytes());
    }
    b
}

/// ffx-rust's platform, answered from Godot through a [`Bridge`].
#[derive(Debug, Clone)]
pub struct GodotPlatform {
    bridge: Arc<Bridge>,
}

impl GodotPlatform {
    /// A platform over `bridge`.
    #[must_use]
    pub fn new(bridge: Arc<Bridge>) -> Self {
        Self { bridge }
    }

    fn b(&self) -> &Bridge {
        &self.bridge
    }
}

impl Platform for GodotPlatform {
    fn name(&self) -> &'static str {
        "godot"
    }

    // ---- the window ---------------------------------------------------------

    fn windowed(&self) -> bool {
        true
    }

    fn set_icon_source(&self, _: &std::path::Path) {}

    fn register_class(&self, _: u32, _: &[u8]) -> u16 {
        0xc0de
    }

    fn create_window(&self, _: &WindowRequest<'_>) -> u32 {
        let b = self.b();
        b.window_made.store(true, Ordering::Release);
        // what Windows sends a new window: its size and the activation
        let (w, h) = *lock(&b.size);
        b.send(WM_SIZE, 0, (word(h) & 0xffff) << 16 | (word(w) & 0xffff));
        if b.focused() {
            b.send(WM_ACTIVATE, 1, 0);
        }
        WINDOW
    }

    fn show_window(&self, _: u32, _: i32) -> i32 {
        0
    }

    fn set_window_pos(&self, _: u32, _: u32, _: i32, _: i32, _: i32, _: i32, _: u32) -> i32 {
        1
    }

    fn client_rect(&self, _: u32) -> (i32, Rect) {
        let (w, h) = *lock(&self.b().size);
        (
            1,
            Rect {
                left: 0,
                top: 0,
                right: w,
                bottom: h,
            },
        )
    }

    fn window_rect(&self, _: u32) -> (i32, Rect) {
        let (w, h) = *lock(&self.b().size);
        let (x, y) = *lock(&self.b().origin);
        (
            1,
            Rect {
                left: x,
                top: y,
                right: x + w,
                bottom: y + h,
            },
        )
    }

    fn adjust_window_rect(&self, _: &mut Rect, _: u32, _: i32) -> i32 {
        // Godot's window has its own frame: the client area is the window
        1
    }

    fn client_to_screen(&self, _: u32, point: &mut Point) -> i32 {
        let (x, y) = *lock(&self.b().origin);
        point.x += x;
        point.y += y;
        1
    }

    fn take_sent(&self) -> Option<[u32; 4]> {
        lock(&self.b().sent).pop_front()
    }

    fn peek_message(&self, window: u32, min: u32, max: u32, remove: u32) -> Option<Message> {
        let mut q = lock(&self.b().posted);
        let any_range = min == 0 && max == 0;
        let at = q.iter().position(|m| {
            (window == 0 || m.window == window || m.message == WM_QUIT)
                && (any_range || (min..=max).contains(&m.message))
        })?;
        if remove & 1 != 0 {
            q.remove(at)
        } else {
            q.get(at).copied()
        }
    }

    fn translate_message(&self, _: &Message) -> i32 {
        0
    }

    fn dispatch_message(&self, _: &Message) -> u32 {
        0
    }

    fn def_window_proc(&self, _: u32, _: u32, _: u32, _: u32) -> u32 {
        0
    }

    fn set_window_long(&self, _: u32, index: i32, value: i32) -> i32 {
        lock(&self.b().longs).insert(index, value).unwrap_or(0)
    }

    fn window_long(&self, _: u32, index: i32) -> i32 {
        lock(&self.b().longs).get(&index).copied().unwrap_or(0)
    }

    fn set_window_text(&self, _: u32, _: &[u8]) -> i32 {
        1
    }

    fn update_window(&self, _: u32) -> i32 {
        1
    }

    fn destroy_window(&self, _: u32) -> i32 {
        1
    }

    fn set_parent(&self, _: u32, _: u32) -> u32 {
        0
    }

    fn post_message(&self, window: u32, message: u32, wparam: u32, lparam: u32) -> i32 {
        lock(&self.b().posted).push_back(Message {
            window,
            message,
            wparam,
            lparam,
            time: 0,
            point: Point::default(),
        });
        1
    }

    fn post_quit_message(&self, code: i32) {
        lock(&self.b().posted).push_back(Message {
            window: 0,
            message: WM_QUIT,
            wparam: word(code),
            ..Message::default()
        });
    }

    fn system_metric(&self, index: i32) -> i32 {
        let (w, h) = *lock(&self.b().screen);
        match index {
            0 | 16 | 78 => w, // SM_CXSCREEN, SM_CXFULLSCREEN, SM_CXVIRTUALSCREEN
            1 | 17 | 79 => h,
            80 => 1, // SM_CMONITORS
            _ => 0,
        }
    }

    fn cursor_pos(&self) -> Point {
        match *lock(&self.b().cursor) {
            Some((x, y)) => {
                let (ox, oy) = *lock(&self.b().origin);
                Point {
                    x: ox + x,
                    y: oy + y,
                }
            }
            // far outside the window, as the headless platform answers
            None => Point {
                x: -32108,
                y: -32131,
            },
        }
    }

    fn set_cursor_pos(&self, _: i32, _: i32) -> i32 {
        1
    }

    fn clip_cursor(&self, rect: Option<Rect>) -> i32 {
        *lock(&self.b().clip) = rect;
        1
    }

    fn clip_rect(&self) -> Rect {
        lock(&self.b().clip).unwrap_or_else(|| self.b().screen_rect())
    }

    fn show_cursor(&self, show: i32) -> i32 {
        let d = if show != 0 { 1 } else { -1 };
        self.b().cursor_count.fetch_add(d, Ordering::AcqRel) + d
    }

    fn timer_period(&self, _: bool, _: u32) -> u32 {
        0
    }

    // ---- graphics -----------------------------------------------------------

    fn graphics(&self) -> Box<dyn ffx_host::gfx::Backend> {
        Box::new(ffx_host::gfx::Null::default())
    }

    // ---- input --------------------------------------------------------------

    fn input_devices(&self) -> bool {
        true
    }

    fn input_open(&self, handle: u32, guid: u128) -> Result<String, i32> {
        let b = self.b();
        let (dev, name) = match guid {
            SYS_KEYBOARD => (Device::Keyboard, "Godot keyboard".to_string()),
            SYS_MOUSE => (Device::Mouse, "Godot mouse".to_string()),
            g => {
                let i = b
                    .connected_pads()
                    .into_iter()
                    .find(|&i| pad_instance(i) == g)
                    .ok_or(DIERR_DEVICENOTREG)?;
                (Device::Pad(i), format!("Godot joypad {i}"))
            }
        };
        lock(&b.devices).insert(handle, dev);
        Ok(name)
    }

    fn input_has(&self, handle: u32) -> bool {
        self.b().device(handle).is_some()
    }

    fn input_release(&self, handle: u32) {
        lock(&self.b().devices).remove(&handle);
    }

    fn input_status(&self, guid: u128) -> Option<i32> {
        let attached = guid == SYS_KEYBOARD
            || guid == SYS_MOUSE
            || self
                .b()
                .connected_pads()
                .into_iter()
                .any(|i| pad_instance(i) == guid);
        Some(if attached { DI_OK } else { DIERR_DEVICENOTREG })
    }

    fn input_controllers(&self, _: u32) -> Vec<Vec<u8>> {
        self.b()
            .connected_pads()
            .into_iter()
            .map(|i| {
                instance(
                    pad_instance(i),
                    pad_product(),
                    DEVTYPE_GAMEPAD,
                    &format!("Controller (Godot joypad {i})"),
                )
            })
            .collect()
    }

    fn input_objects(&self, handle: u32, _: u32) -> Option<Vec<Vec<u8>>> {
        self.b().device(handle).map(|_| Vec::new())
    }

    fn input_set_format(&self, handle: u32, _: &ffx_host::input::Format) -> Option<i32> {
        self.b().device(handle).map(|_| DI_OK)
    }

    fn input_set_cooperative_level(&self, handle: u32, _: u32, _: u32) -> Option<i32> {
        self.b().device(handle).map(|_| DI_OK)
    }

    fn input_property(&self, handle: u32, _: &Property, _: &mut [u8], set: bool) -> Option<i32> {
        self.b()
            .device(handle)
            .map(|_| if set { DI_OK } else { DIERR_UNSUPPORTED })
    }

    fn input_call(&self, handle: u32, call: DeviceCall) -> Option<i32> {
        let dev = self.b().device(handle)?;
        let pad = matches!(dev, Device::Pad(_));
        Some(match call {
            DeviceCall::Acquire if !pad && !self.b().focused() => DIERR_OTHERAPPHASPRIO,
            DeviceCall::Acquire | DeviceCall::Unacquire => DI_OK,
            DeviceCall::Poll => DI_NOEFFECT,
        })
    }

    fn input_state(&self, handle: u32, size: usize) -> Option<Result<Vec<u8>, i32>> {
        let b = self.b();
        let dev = b.device(handle)?;
        if !matches!(dev, Device::Pad(_)) && !b.focused() {
            return Some(Err(DIERR_INPUTLOST));
        }
        Some(match dev {
            Device::Keyboard => {
                let keys = lock(&b.keys);
                let mut s = keys.to_vec();
                s.resize(size, 0);
                Ok(s)
            }
            Device::Mouse => {
                // DIMOUSESTATE(2): lX, lY, lZ, then the buttons
                let mut m = lock(&b.mouse);
                let mut s = Vec::with_capacity(20);
                for v in [m.dx, m.dy, m.dz] {
                    s.extend_from_slice(&v.to_le_bytes());
                }
                s.extend(m.buttons.iter().map(|&d| if d { 0x80 } else { 0 }));
                m.dx = 0;
                m.dy = 0;
                m.dz = 0;
                s.resize(size, 0);
                Ok(s)
            }
            // XInput pads are read through XInput; their DirectInput
            // state stays neutral
            Device::Pad(_) => Ok(vec![0; size]),
        })
    }

    fn input_data(&self, handle: u32, _: u32, _: u32) -> Option<Result<(Vec<[u32; 5]>, i32), i32>> {
        let b = self.b();
        let dev = b.device(handle)?;
        if !matches!(dev, Device::Pad(_)) && !b.focused() {
            return Some(Err(DIERR_NOTACQUIRED));
        }
        Some(Ok((Vec::new(), DI_OK)))
    }

    fn input_info(&self, handle: u32, what: DeviceInfo, data: &mut [u8]) -> Option<i32> {
        let dev = self.b().device(handle)?;
        let (guid, product, devtype, name, axes, buttons) = match dev {
            Device::Keyboard => (
                SYS_KEYBOARD,
                SYS_KEYBOARD,
                DEVTYPE_KEYBOARD,
                "Keyboard",
                0,
                128,
            ),
            Device::Mouse => (SYS_MOUSE, SYS_MOUSE, DEVTYPE_MOUSE, "Mouse", 3, 8),
            Device::Pad(i) => (
                pad_instance(i),
                pad_product(),
                DEVTYPE_GAMEPAD,
                "Controller",
                6,
                14,
            ),
        };
        match what {
            DeviceInfo::Capabilities => {
                // DIDEVCAPS: size, flags (DIDC_ATTACHED), type, axes,
                // buttons, POVs
                let words = [
                    0x2cu32,
                    1,
                    devtype,
                    axes,
                    buttons,
                    u32::from(dev != Device::Keyboard && dev != Device::Mouse),
                ];
                for (i, w) in words.iter().enumerate().skip(1) {
                    if data.len() >= 4 * i + 4 {
                        data[4 * i..4 * i + 4].copy_from_slice(&w.to_le_bytes());
                    }
                }
            }
            DeviceInfo::Instance => {
                let inst = instance(guid, product, devtype, name);
                let n = data.len().min(inst.len());
                // keep the game's dwSize
                if n > 4 {
                    data[4..n].copy_from_slice(&inst[4..n]);
                }
            }
        }
        Some(DI_OK)
    }

    fn xinput_state(&self, user: u32) -> Result<[u8; 16], u32> {
        let pads = lock(&self.b().pads);
        let slot = pads
            .get(user as usize)
            .ok_or(ffx_host::input::NOT_CONNECTED)?;
        slot.pad
            .map(|p| p.state(slot.packet))
            .ok_or(ffx_host::input::NOT_CONNECTED)
    }

    fn xinput_set_state(&self, user: u32, vibration: [u8; 4]) -> u32 {
        let mut pads = lock(&self.b().pads);
        match pads.get_mut(user as usize) {
            Some(slot) if slot.pad.is_some() => {
                slot.vibration = [
                    u16::from_le_bytes([vibration[0], vibration[1]]),
                    u16::from_le_bytes([vibration[2], vibration[3]]),
                ];
                0
            }
            _ => ffx_host::input::NOT_CONNECTED,
        }
    }

    fn xinput_ids(&self) -> Vec<(u32, u32)> {
        if self.b().connected_pads().is_empty() {
            Vec::new()
        } else {
            vec![(PAD_VENDOR, PAD_PRODUCT)]
        }
    }

    // ---- sound: the queue Godot's player pulls from ---------------------------

    fn audio_start(&self) {
        self.b().audio.start();
        ffx_host::log::note(
            "rust audio out: a queue for Godot's FfxAudioOut (48000 Hz stereo); \
             it runs while that player is attached, else the mix follows the wall clock",
        );
    }

    fn audio_running(&self) -> bool {
        self.b().audio.running()
    }

    fn audio_queued(&self) -> usize {
        self.b().audio.queued()
    }

    fn audio_underruns(&self) -> u64 {
        self.b().audio.underruns()
    }

    fn audio_push(&self, block: &[f32]) {
        self.b().audio.push(block);
    }

    // ---- Steam, folders, dialogs, the process ---------------------------------

    fn steam(&self) -> Result<Box<dyn ffx_host::steam::Backend>, String> {
        Err("no Steam in the Godot frontend yet".into())
    }

    fn documents_folder(&self) -> Option<std::path::PathBuf> {
        // the frontend passes its own sandbox to the boot; never the
        // player's documents folder
        None
    }

    fn message_box(&self, title: &str, text: &str, _: bool) {
        eprintln!("{title}: {text}");
        lock(&self.b().boxes).push((title.to_string(), text.to_string()));
    }

    fn end_process(&self, code: u32) -> ! {
        #[allow(clippy::cast_possible_wrap)] // an exit code's bits
        std::process::exit(code as i32)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guids_round_trip_as_the_game_reads_them() {
        let b = guid_bytes(SYS_KEYBOARD);
        // {6F1D2B61-D5A0-11CF-BFC7-444553540000}
        assert_eq!(
            b,
            [
                0x61, 0x2b, 0x1d, 0x6f, 0xa0, 0xd5, 0xcf, 0x11, 0xbf, 0xc7, 0x44, 0x45, 0x53, 0x54,
                0x00, 0x00
            ]
        );
        // Data1 of the product GUID: vendor | product << 16
        let p = guid_bytes(pad_product());
        assert_eq!(
            u32::from_le_bytes([p[0], p[1], p[2], p[3]]),
            PAD_VENDOR | PAD_PRODUCT << 16
        );
        assert_eq!(&p[10..], b"PIDVID");
    }

    #[test]
    fn the_keyboard_reads_only_with_the_focus() {
        let bridge = Arc::new(Bridge::default());
        let p = GodotPlatform::new(Arc::clone(&bridge));
        assert!(p.input_open(0x10, SYS_KEYBOARD).is_ok());
        bridge.key(0x30, true);
        let s = p.input_state(0x10, 256).unwrap().unwrap();
        assert_eq!(s[0x30], 0x80);
        bridge.set_focus(false);
        assert_eq!(p.input_state(0x10, 256), Some(Err(DIERR_INPUTLOST)));
        bridge.set_focus(true);
        // the keys were let go with the focus
        assert_eq!(p.input_state(0x10, 256).unwrap().unwrap()[0x30], 0);
    }

    #[test]
    fn window_events_become_the_games_messages() {
        let bridge = Arc::new(Bridge::default());
        let p = GodotPlatform::new(Arc::clone(&bridge));
        bridge.resize(1280, 720);
        assert_eq!(p.take_sent(), None, "no window yet");
        let req = WindowRequest {
            ex_style: 0,
            class: b"c\0",
            title: b"t\0",
            style: 0,
            x: 0,
            y: 0,
            width: 0,
            height: 0,
        };
        assert_eq!(p.create_window(&req), WINDOW);
        assert_eq!(
            p.take_sent(),
            Some([WINDOW, WM_SIZE, 0, (720 << 16) | 0x500])
        );
        assert_eq!(p.take_sent(), Some([WINDOW, WM_ACTIVATE, 1, 0]));
        bridge.close();
        assert_eq!(p.take_sent(), Some([WINDOW, WM_CLOSE, 0, 0]));
        assert_eq!(p.take_sent(), None);
    }

    #[test]
    fn a_pad_is_an_xinput_controller() {
        let bridge = Arc::new(Bridge::default());
        let p = GodotPlatform::new(Arc::clone(&bridge));
        assert_eq!(p.input_controllers(0).len(), 0);
        bridge.set_pad(
            0,
            Some(Pad {
                buttons: 0x1000,
                ..Pad::default()
            }),
        );
        let c = p.input_controllers(0);
        assert_eq!(c.len(), 1);
        assert_eq!(c[0].len(), 0x244);
        assert_eq!(p.xinput_ids(), vec![(PAD_VENDOR, PAD_PRODUCT)]);
        let s = p.xinput_state(0).unwrap();
        assert_eq!(u16::from_le_bytes([s[4], s[5]]), 0x1000);
        assert!(p.xinput_state(1).is_err());
    }

    /// One of the mixer's blocks (1,600 frames), left `v`, right -`v`.
    fn block(v: f32) -> Vec<f32> {
        (0..1600).flat_map(|_| [v, -v]).collect()
    }

    /// A queue the game started, with a player attached.
    fn running_queue() -> AudioQueue {
        let q = AudioQueue::default();
        q.start();
        q.attach(true);
        q
    }

    #[test]
    fn audio_runs_only_with_a_player_attached() {
        let bridge = Arc::new(Bridge::default());
        let p = GodotPlatform::new(Arc::clone(&bridge));
        p.audio_start();
        assert!(
            !p.audio_running(),
            "no player: the feeder follows the wall clock"
        );
        p.audio_push(&block(0.5));
        assert_eq!(p.audio_queued(), 0, "dropped while not running");
        bridge.audio.attach(true);
        assert!(p.audio_running());
        p.audio_push(&block(0.5));
        assert_eq!(p.audio_queued(), 1600);
        bridge.audio.attach(false);
        assert!(!p.audio_running());
        assert_eq!(p.audio_queued(), 0, "the queue goes with the player");
    }

    /// What the device asks for at once: `frames` frames, interleaved.
    fn ask(q: &AudioQueue, frames: usize) -> (usize, Vec<f32>) {
        let mut out = vec![9.0; 2 * frames];
        let n = q.fill(&mut out);
        (n, out)
    }

    #[test]
    fn audio_waits_for_the_prefill_then_fills_in_order() {
        let q = running_queue();
        q.push(&block(0.1));
        q.push(&block(0.2));
        let (n, out) = ask(&q, 128);
        assert_eq!(n, 0, "under the prefill");
        assert!(out.iter().all(|&s| s == 0.0), "silence, all of it");
        assert_eq!(q.underruns(), 0, "waiting for the prefill is no underrun");
        q.push(&block(0.3));
        assert_eq!(q.queued(), AUDIO_PREFILL);
        let (n, out) = ask(&q, 1000);
        assert_eq!(n, 1000);
        assert!(out.chunks(2).all(|f| f == [0.1, -0.1]));
        // on from the first block into the second, left and right in place
        let (n, out) = ask(&q, 1000);
        assert_eq!(n, 1000);
        assert_eq!(&out[..2], &[0.1, -0.1]);
        assert_eq!(&out[1200..1202], &[0.2, -0.2]);
        assert_eq!(q.queued(), 2800);
        assert_eq!(q.underruns(), 0);
        assert_eq!(q.stats().pulled, 2000);
    }

    #[test]
    fn audio_mean_is_the_queues_time_average_while_playing() {
        let q = running_queue();
        let t0 = Instant::now();
        let at = |ms: u64| t0 + std::time::Duration::from_millis(ms);
        let mut out = vec![0.0; 2 * 1000];
        // filling up to the prefill: not playing, not counted
        for (ms, v) in [(0, 0.1), (10, 0.2), (20, 0.3)] {
            q.push_at(&block(v), at(ms));
        }
        assert_eq!(q.fill_at(&mut out, at(100)), 1000);
        assert_eq!(q.stats().mean_queued, 0, "nothing played for a while yet");
        // 3,800 frames for 30 ms, then 5,400 for 10 ms
        q.push_at(&block(0.4), at(130));
        assert_eq!(q.fill_at(&mut out, at(140)), 1000);
        assert_eq!(q.stats().mean_queued, (3800 * 30 + 5400 * 10) / 40);
        // a burst of asks (Godot's dummy driver: 4,096 frames at once)
        // weighs by time, not by the number of asks
        let mut big = vec![0.0; 2 * 4096];
        assert_eq!(q.fill_at(&mut big, at(240)), 4096);
        for _ in 0..8 {
            q.fill_at(&mut out[..2], at(240));
        }
        // (3,800 * 30 + 5,400 * 10 + 4,400 * 100) / 140 = 4,342.9
        assert_eq!(q.stats().mean_queued, 4343);
        // a player attached anew starts its mean anew
        q.attach(true);
        assert_eq!(q.stats().mean_queued, 0);
    }

    #[test]
    fn audio_counts_an_underrun_when_the_device_asks_for_more() {
        let q = running_queue();
        for v in [0.1, 0.2, 0.3] {
            q.push(&block(v));
        }
        assert_eq!(ask(&q, 4000).0, 4000);
        // 800 left, 1,000 asked: what is there, then silence, an underrun
        let (n, out) = ask(&q, 1000);
        assert_eq!(n, 800);
        assert_eq!(&out[1598..1600], &[0.3, -0.3]);
        assert!(out[1600..].iter().all(|&s| s == 0.0));
        assert_eq!(q.underruns(), 1);
        assert_eq!(q.queued(), 0);
        // then silence until the prefill again, no more underruns
        q.push(&block(0.4));
        assert_eq!(ask(&q, 100).0, 0);
        assert_eq!(ask(&q, 100).0, 0);
        assert_eq!(q.underruns(), 1);
        q.push(&block(0.5));
        q.push(&block(0.6));
        let (n, out) = ask(&q, 100);
        assert_eq!(n, 100);
        assert_eq!(&out[198..], &[0.4, -0.4]);
        assert_eq!(q.stats().pulled, 4900);
    }

    #[test]
    fn audio_device_pulls_keep_the_queue_at_the_feeders_target() {
        // the feeder as ffx-rust runs it (feed_due: blocks while queued <
        // 6,400), Godot's mix in 128-frame chunks (its resampler's): the
        // queue, all our buffering, stays between the target and a block
        // over, with no underrun
        const TARGET: usize = 4800 + 1600;
        let q = running_queue();
        let (mut low, mut high) = (usize::MAX, 0);
        for step in 0..2000 {
            while q.queued() < TARGET {
                q.push(&block(0.5));
            }
            if step > 0 {
                low = low.min(q.queued());
                high = high.max(q.queued());
            }
            // the device takes ~4 ms between two looks of the feeder
            for _ in 0..=step % 2 {
                assert_eq!(ask(&q, 128).0, 128);
            }
        }
        assert_eq!(q.underruns(), 0);
        assert!(low >= TARGET && high < TARGET + 1600, "{low}..{high}");
    }

    #[test]
    fn audio_keeps_half_a_second_dropping_the_oldest() {
        let q = running_queue();
        // 16 blocks: 25,600 frames, one block over
        for i in 0..16u8 {
            q.push(&block(f32::from(i)));
        }
        assert_eq!(q.queued(), AUDIO_MAX_QUEUE);
        assert_eq!(q.stats().dropped, 1600);
        let (n, out) = ask(&q, 2);
        assert_eq!(n, 2);
        assert_eq!(out, [1.0, -1.0, 1.0, -1.0], "block 0 was dropped");
        let (n, out) = ask(&q, AUDIO_MAX_QUEUE - 2);
        assert_eq!(n, AUDIO_MAX_QUEUE - 2);
        assert_eq!(&out[out.len() - 2..], &[15.0, -15.0]);
        assert_eq!(q.underruns(), 0);
    }

    #[test]
    fn audio_attach_and_stop_start_empty() {
        let q = running_queue();
        for v in [0.1, 0.2, 0.3] {
            q.push(&block(v));
        }
        assert_eq!(ask(&q, 100).0, 100);
        // a player attached anew gets no stale tail
        q.attach(true);
        assert_eq!(q.queued(), 0);
        for v in [0.1, 0.2, 0.3] {
            q.push(&block(v));
        }
        assert_eq!(ask(&q, 800).0, 800);
        // the game ends: the queue goes, nothing more comes, and the
        // silence after is no underrun
        q.stop();
        assert!(!q.running());
        assert_eq!(q.queued(), 0);
        q.push(&block(0.4));
        assert_eq!(q.queued(), 0);
        let (n, out) = ask(&q, 800);
        assert_eq!(n, 0);
        assert!(out.iter().all(|&s| s == 0.0));
        assert_eq!(q.underruns(), 0);
    }
}
