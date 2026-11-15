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
//! - **Sound** is silent for now (G3 routes the game's sound to Godot);
//!   **Steam** is absent; the **documents folder** is the sandbox the
//!   frontend passes to the boot, never the player's own.

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::{Arc, Mutex};

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

    // ---- sound (silent until G3) ----------------------------------------------

    fn audio_start(&self) {
        ffx_host::log::note("rust audio out: none in the Godot frontend yet (silent)");
    }

    fn audio_running(&self) -> bool {
        false
    }

    fn audio_queued(&self) -> usize {
        0
    }

    fn audio_underruns(&self) -> u64 {
        0
    }

    fn audio_push(&self, _: &[f32]) {}

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
}
