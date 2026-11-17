//! The ESC menu's Flash callbacks, driven by gd-ffx's native page
//! (docs/architecture.md, "The ESC menu's Change parameters").
//!
//! The game's PC menu (`g_FFEscMenu`, `escmenu.swf`) hears its page only
//! through the AS3 callbacks the movie calls (`ExternalInterface.call`):
//! `EscMenuButtonClick([page, index])`, `EscMenuPopUpConfirmed(answer)`
//! (ffx-rust's docs/formats/pc-ui.md, "Iggy calls"). Without Ruffle the
//! movie is a null player and calls nothing, so gd-ffx calls them in its
//! place, through the game's own callback dispatcher
//! (`ffx_host::iggy::as3_call`), on the game's main thread: a wrapper of
//! `FFEscMenu_update` (`0x0068ffb0`, once a frame while the menu exists),
//! registered in the function table at the boot (`Table::wrap`, the
//! table's modding seam), runs the queued calls and then the original,
//! whose handler flush (`IggyMenuHandlers_flush`) runs what they queued.
//!
//! Change parameters (page 13): row `k` (0 every item, 1 every ability, 2
//! the most gil) is `EscMenuButtonClick([13, k])`:
//! `FFEscMenu_onParameterButton_0068f610` stores `k` in
//! `g_EscParameterButtonArg` and queues
//! `FFEscMenu_onParameterButtonClicked` (`0x006fc7b0`), which shows the
//! confirmation (texts 0x2c9..0x2cb) and sets its answer
//! `FFEscMenu_onDebugFill_006fc970` at `g_FFEscMenu + 0x10c` when no movie
//! fades, the scenario flag's low 16 bits are set and the map is not an
//! Eternal Calm scene; else it shows "Function not Available now!". The
//! answer is `EscMenuPopUpConfirmed(1)` (yes: `MsDebugFullItem`,
//! achievements 0x19 and 0x18 then `MsDebugFullCommand`, or
//! `MsDebugFullGill`) or `(0)`. While the menu is open the wrapper also
//! works out the condition the page's entry is enabled by
//! (`FFEscMenu_refreshParameterAndSpecialPageEnableState`'s
//! `setEnableChangePara`), with the game's own functions.

use std::collections::VecDeque;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU32, Ordering};

use ffx_bridge::Mem as _;
use ffx_host::exec::Exec;
use ffx_host::iggy::Value;
use ffx_host::table::{Args, Conv, Entry, Port, Ret, Table};

/// `FFEscMenu_update(this)` (`ecx`, plain ret).
const UPDATE: u32 = 0x0068_ffb0;
/// Its first instructions: `push ebp; mov ebp, esp; sub esp, 0x18`
/// (checked against the image when a `dev-unicorn` build makes its
/// trampoline).
const UPDATE_PROLOGUE: &[u8] = &[0x55, 0x8b, 0xec, 0x83, 0xec, 0x18];
/// `IggyMenu + 0x6c`: the player block; its first word is the player
/// (`GetDerefU32_00a28d10`), which the callback dispatcher matches.
const MENU_PLAYER: u32 = 0x6c;
/// `IggyMenu + 0x70`: the menu is open.
const MENU_OPEN: u32 = 0x70;
/// `g_FFEscMenu + 0x10c`: the open pop-up's answer callback (0 none).
const POPUP_CALLBACK: u32 = 0x10c;
/// `FFEscMenu_onDebugFill_006fc970`: the parameter page's answer.
const ON_DEBUG_FILL: u32 = 0x006f_c970;
/// `g_EscParameterButtonArg`: the row the page's button was for.
const PARAMETER_ARG: u32 = 0x00cc_c884;
/// The parameter page's id in `EscMenuButtonClick` and
/// `EscMenuHelpTextRefresh` (`FFEscMenu_onParameterButton_0068f610`
/// takes only 13).
pub const PARAMETER_PAGE: u32 = 13;
/// What the page's entry is enabled by (`FFEscMenu_onParameterButtonClicked`):
/// `g_MovieFading`, `g_CurrentMapNo`, `getScenerioFlag()`,
/// `isEternalCalmScene(map)`.
const MOVIE_FADING: u32 = 0x0134_0824;
const CURRENT_MAP: u32 = 0x012f_bc40;
const SCENARIO_FLAG: u32 = 0x0078_7360;
const IS_ETERNAL_CALM: u32 = 0x0067_ab50;

/// A call for the menu's callbacks.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Call {
    /// `EscMenuButtonClick([page, index])`.
    Button { page: u32, index: u32 },
    /// `EscMenuPopUpConfirmed(answer)`: 1 yes, 0 no.
    PopUp { answer: f64 },
}

/// The calls waiting for the next `FFEscMenu_update`.
static QUEUE: Mutex<VecDeque<Call>> = Mutex::new(VecDeque::new());

/// What the wrapper saw last, for the frontend (bits of [`state`]).
static STATE: AtomicU32 = AtomicU32::new(0);
/// Calls run so far (a reply counter: `done` in [`state`]).
static DONE: AtomicU32 = AtomicU32::new(0);
/// The row of the last button the game took (`g_EscParameterButtonArg`).
static ROW: AtomicU32 = AtomicU32::new(u32::MAX);

const S_SEEN: u32 = 1;
const S_OPEN: u32 = 2;
const S_PARAMETERS: u32 = 4;
const S_POPUP: u32 = 8;
const S_FAILED: u32 = 0x10;

/// Queues `call` for the game's main thread (the next `FFEscMenu_update`).
pub fn push(call: Call) {
    if let Ok(mut q) = QUEUE.lock() {
        q.push_back(call);
    }
}

/// What the wrapper saw at the last `FFEscMenu_update`: whether it ran at
/// all (`seen`), the menu is open, the Change parameters entry is enabled,
/// the page's confirmation waits for its answer (`popup`), a callback
/// could not be called (`failed`: no AS3 callback registered), the calls
/// run so far (`done`) and the row of the last button the game took.
#[must_use]
pub fn state() -> (u32, u32, u32) {
    (
        STATE.load(Ordering::Acquire),
        DONE.load(Ordering::Acquire),
        ROW.load(Ordering::Acquire),
    )
}

/// Bits of [`state`]'s first value.
#[must_use]
pub const fn bits(s: u32) -> [bool; 5] {
    [
        s & S_SEEN != 0,
        s & S_OPEN != 0,
        s & S_PARAMETERS != 0,
        s & S_POPUP != 0,
        s & S_FAILED != 0,
    ]
}

/// Wraps `FFEscMenu_update` in `table` (before the boot).
pub fn register(table: &mut Table) {
    table.wrap(
        UPDATE,
        Entry {
            name: "FFEscMenu_update (gd-ffx: the native ESC menu's calls)",
            conv: Conv::Thiscall,
            words: 0,
            port: Port::Exec(update),
        },
        UPDATE_PROLOGUE,
    );
}

/// The wrapper: the queued calls, the original, then what the page shows.
fn update(e: &mut Exec, args: &Args) -> Ret {
    let menu = args.ecx;
    let calls: Vec<Call> = QUEUE
        .lock()
        .map(|mut q| q.drain(..).collect())
        .unwrap_or_default();
    let mut failed = false;
    let ran = u32::try_from(calls.len()).unwrap_or(0);
    if !calls.is_empty() && menu != 0 {
        let player = {
            let mem = e.mem();
            let block: u32 = mem.read(menu + MENU_PLAYER);
            if block == 0 { 0 } else { mem.read::<u32>(block) }
        };
        for call in calls {
            let (name, values) = match call {
                Call::Button { page, index } => (
                    "EscMenuButtonClick",
                    vec![Value::List(vec![
                        Value::Number(f64::from(page)),
                        Value::Number(f64::from(index)),
                    ])],
                ),
                Call::PopUp { answer } => ("EscMenuPopUpConfirmed", vec![Value::Number(answer)]),
            };
            // the answer is `None` both without a callback and when the
            // game set none (these callbacks answer nothing): tell them
            // apart by the callback's registration
            let _ = ffx_host::iggy::as3_call(e, player, name, &values);
            let key = format!("as3 {name}");
            if e.shared().iggy.calls.lock().is_ok_and(|c| !c.contains_key(&key)) {
                failed = true;
            }
            ffx_host::log::note(&format!("gd-ffx: ESC menu {call:?} -> {name}"));
        }
    }
    let ret = e
        .call_original(UPDATE, Conv::Thiscall, args)
        .unwrap_or(Ret::VOID);
    if menu != 0 {
        let (open, popup, row) = {
            let mem = e.mem();
            (
                mem.read::<u8>(menu + MENU_OPEN) != 0,
                mem.read::<u32>(menu + POPUP_CALLBACK) == ON_DEBUG_FILL,
                mem.read::<u32>(PARAMETER_ARG),
            )
        };
        let mut s = S_SEEN;
        if open {
            s |= S_OPEN;
            if parameters_enabled(e) {
                s |= S_PARAMETERS;
            }
        }
        if popup {
            s |= S_POPUP;
        }
        if failed || STATE.load(Ordering::Acquire) & S_FAILED != 0 {
            s |= S_FAILED;
        }
        STATE.store(s, Ordering::Release);
        ROW.store(row, Ordering::Release);
    }
    // counted once what they did shows in the state
    DONE.fetch_add(ran, Ordering::AcqRel);
    ret
}

/// `FFEscMenu_refreshParameterAndSpecialPageEnableState`'s condition for
/// `setEnableChangePara` (and `onParameterButtonClicked`'s): no movie
/// fading, the scenario flag's low 16 bits set, not an Eternal Calm scene.
fn parameters_enabled(e: &mut Exec) -> bool {
    let (fading, map) = {
        let mem = e.mem();
        (mem.read::<u32>(MOVIE_FADING), mem.read::<u32>(CURRENT_MAP))
    };
    if fading != 0 {
        return false;
    }
    // the flag's low 16 bits all clear
    if e.call_cdecl(SCENARIO_FLAG, &[]).unwrap_or(0).trailing_zeros() >= 16 {
        return false;
    }
    e.call_cdecl(IS_ETERNAL_CALM, &[map]).unwrap_or(1) == 0
}
