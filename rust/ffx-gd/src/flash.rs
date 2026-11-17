//! The Flash (Iggy) screens' traffic, for gd-ffx's native screens
//! (docs/architecture.md, "The Flash screens").
//!
//! Without Ruffle the host's Iggy players are null: they play nothing and
//! call nothing. gd-ffx draws every screen natively, from what the game
//! tells the movies. Three of the game's functions are wrapped in the
//! function table at the boot (`Table::wrap`, as `crate::esc` does):
//!
//! - `IggyMenu_ctor_00a288f0(menu; path, x, y, w, h, flags)`: every movie
//!   the game opens (`IggyPlayerOpen*`, the Esc and pause menus, the save
//!   lists, the tips, the icons, the opening, the autosave prompt) is
//!   noted by name with its menu and player; whether the game draws it
//!   (asks it to play) comes from the host's players' draw counts
//!   ([`sample`]).
//! - `IggyMenu_callIggyAS3External(menu; target, name, n, values, unused)`:
//!   every AS3 function the game calls on a movie (`OnPlay` on the tips,
//!   `addSaveLoadItem` on the save list, ...) is queued with its arguments
//!   for the native screens ([`take_calls`]).
//! - `PhyreScene_isOpeningScreenPlaying()`: the start-up loop waits while
//!   it answers 1 for the opening movie's `Finish` callback; once gd-ffx's
//!   native opening has played ([`finish_opening`]) the wrapper calls
//!   `Finish` through the game's own dispatcher (`ffx_host::iggy::as3_call`),
//!   whose handler (`PhyreScene_onOpeningScreenFinish_00652400`) ends the
//!   loop.

use std::collections::{HashMap, VecDeque};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};

use ffx_bridge::Mem as _;
use ffx_host::exec::Exec;
use ffx_host::table::{Args, Conv, Entry, Port, Ret, Table};

/// `IggyMenu_ctor_00a288f0` (thiscall, 6 words): `push ebp; mov ebp, esp;
/// push -1`.
const MENU_CTOR: u32 = 0x00a2_88f0;
const MENU_CTOR_PROLOGUE: &[u8] = &[0x55, 0x8b, 0xec, 0x6a, 0xff];
/// `IggyMenu_callIggyAS3External` (thiscall, 5 words): `push ebp; mov ebp,
/// esp; mov eax, [ecx + 0x6c]`.
const CALL_AS3: u32 = 0x00a2_8ba0;
const CALL_AS3_PROLOGUE: &[u8] = &[0x55, 0x8b, 0xec, 0x8b, 0x41, 0x6c];
/// `PhyreScene_isOpeningScreenPlaying` (cdecl): `mov al, [0x00ccb9c2]`.
const OPENING_PLAYING_FN: u32 = 0x0066_0400;
const OPENING_PLAYING_PROLOGUE: &[u8] = &[0xa0, 0xc2, 0xb9, 0xcc, 0x00];
/// `g_PhyreSceneOpeningPlaying`.
const OPENING_PLAYING: u32 = 0x00cc_b9c2;
/// `IggyMenu + 0x6c`: the player block; its first word the player.
const MENU_PLAYER: u32 = 0x6c;
/// The opening's movie.
pub const OPENING_MOVIE: &str = "openingscreen.swf";
/// The longest the start-up loop waits for the native opening before the
/// wrapper ends it itself (a frontend that never asks), in its calls (about
/// 30 a second: the loop sleeps 33 ms a frame).
const OPENING_LIMIT: u32 = 30 * 30;

/// A movie the game opened.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Movie {
    /// The `.swf` file's name, lower case (`chocobo.swf`).
    pub name: String,
    /// The `IggyMenu` and its player.
    pub menu: u32,
    pub player: u32,
    /// Opened this many-th (from 1), among all movies.
    pub seq: u32,
    /// The host's draws of its player so far (asked to play: drawn).
    pub draws: u64,
}

/// An AS3 call the game made on a movie.
#[derive(Debug, Clone, PartialEq)]
pub struct Call {
    /// The movie's name ("" when its menu is not known) and open sequence.
    pub movie: String,
    pub seq: u32,
    pub function: String,
    /// The arguments as text: strings as they are, numbers in the shortest
    /// form, booleans `true`/`false`, anything else empty.
    pub args: Vec<String>,
}

static MOVIES: Mutex<Vec<Movie>> = Mutex::new(Vec::new());
static CALLS: Mutex<VecDeque<Call>> = Mutex::new(VecDeque::new());
/// Calls dropped because nobody took them (the queue is bounded).
static DROPPED: AtomicU32 = AtomicU32::new(0);
const MAX_CALLS: usize = 4096;
/// Whether the native opening asked its end.
static FINISH: AtomicBool = AtomicBool::new(false);
/// The opening's state bits ([`opening_state`]) and the loop's calls.
static OPENING: AtomicU32 = AtomicU32::new(0);
static OPENING_POLLS: AtomicU32 = AtomicU32::new(0);

/// [`opening_state`]'s bits: the start-up loop waited for the opening; the
/// wrapper called `Finish`; the loop ended; the wrapper ended it on its own
/// (no frontend asked within [`OPENING_LIMIT`]).
pub const O_WAITING: u32 = 1;
pub const O_FINISH_SENT: u32 = 2;
pub const O_ENDED: u32 = 4;
pub const O_TIMED_OUT: u32 = 8;

/// Wraps the three functions in `table` (before the boot).
pub fn register(table: &mut Table) {
    for (addr, name, conv, words, port, prologue) in [
        (
            MENU_CTOR,
            "IggyMenu_ctor (gd-ffx: the movies opened)",
            Conv::Thiscall,
            6,
            Port::Exec(menu_ctor),
            MENU_CTOR_PROLOGUE,
        ),
        (
            CALL_AS3,
            "IggyMenu_callIggyAS3External (gd-ffx: the movies' calls)",
            Conv::Thiscall,
            5,
            Port::Exec(call_as3),
            CALL_AS3_PROLOGUE,
        ),
        (
            OPENING_PLAYING_FN,
            "PhyreScene_isOpeningScreenPlaying (gd-ffx: the native opening's Finish)",
            Conv::Cdecl,
            0,
            Port::Exec(opening_playing),
            OPENING_PLAYING_PROLOGUE,
        ),
    ] {
        if table.get(addr).is_none() {
            ffx_host::log::note(&format!(
                "gd-ffx: {addr:#010x} has no port to wrap ({name})"
            ));
        }
        table.wrap(
            addr,
            Entry {
                name,
                conv,
                words,
                port,
            },
            prologue,
        );
    }
}

/// A C string in game memory (at most `max` bytes).
fn c_string(e: &Exec, at: u32, max: u32) -> String {
    if at == 0 {
        return String::new();
    }
    let mem = e.mem();
    let mut b = Vec::new();
    for i in 0..max {
        let c: u8 = mem.read(at + i);
        if c == 0 {
            break;
        }
        b.push(c);
    }
    String::from_utf8_lossy(&b).into_owned()
}

/// A path's file name, lower case.
fn file_name(path: &str) -> String {
    path.rsplit(['/', '\\'])
        .next()
        .unwrap_or(path)
        .to_ascii_lowercase()
}

fn menu_ctor(e: &mut Exec, args: &Args) -> Ret {
    let path = c_string(e, args.stack.first().copied().unwrap_or(0), 512);
    let ret = e
        .call_original(MENU_CTOR, Conv::Thiscall, args)
        .unwrap_or(Ret::eax(args.ecx));
    let menu = args.ecx;
    let player = {
        let mem = e.mem();
        let block: u32 = mem.read(menu + MENU_PLAYER);
        if block == 0 {
            0
        } else {
            mem.read::<u32>(block)
        }
    };
    let name = file_name(&path);
    if let Ok(mut m) = MOVIES.lock() {
        let seq = u32::try_from(m.len() + 1).unwrap_or(u32::MAX);
        ffx_host::log::note(&format!(
            "gd-ffx: flash movie {seq} opened: {name} (menu {menu:#x}, player {player:#x})"
        ));
        m.push(Movie {
            name,
            menu,
            player,
            seq,
            draws: 0,
        });
    }
    ret
}

/// An `IggyDataValue` (16 bytes) as text.
fn value_text(e: &Exec, at: u32) -> String {
    let mem = e.mem();
    match mem.read::<u32>(at) {
        3 => (mem.read::<u8>(at + 8) != 0).to_string(),
        4 => {
            let n: f64 = mem.read(at + 8);
            if n.fract() == 0.0 && n.abs() < 1e15 {
                format!("{n:.0}")
            } else {
                n.to_string()
            }
        }
        5 => {
            let (text, len) = (mem.read::<u32>(at + 8), mem.read::<u32>(at + 12));
            let mut b = vec![0u8; len.min(1 << 16) as usize];
            mem.read_bytes(text, &mut b);
            String::from_utf8_lossy(&b).into_owned()
        }
        6 => {
            let (text, len) = (mem.read::<u32>(at + 8), mem.read::<u32>(at + 12));
            let units: Vec<u16> = (0..len.min(1 << 15))
                .map(|k| mem.read(text + 2 * k))
                .collect();
            String::from_utf16_lossy(&units)
        }
        _ => String::new(),
    }
}

fn call_as3(e: &mut Exec, args: &Args) -> Ret {
    let menu = args.ecx;
    let word = |i: usize| args.stack.get(i).copied().unwrap_or(0);
    let function = c_string(e, word(1), 256);
    let values: Vec<String> = (0..word(2).min(64))
        .map(|i| value_text(e, word(3) + 16 * i))
        .collect();
    let (movie, seq) = MOVIES
        .lock()
        .ok()
        .and_then(|m| {
            m.iter()
                .rev()
                .find(|x| x.menu == menu)
                .map(|x| (x.name.clone(), x.seq))
        })
        .unwrap_or_default();
    if let Ok(mut q) = CALLS.lock() {
        if q.len() >= MAX_CALLS {
            q.pop_front();
            DROPPED.fetch_add(1, Ordering::Relaxed);
        }
        q.push_back(Call {
            movie,
            seq,
            function,
            args: values,
        });
    }
    e.call_original(CALL_AS3, Conv::Thiscall, args)
        .unwrap_or(Ret::eax(0))
}

fn opening_playing(e: &mut Exec, args: &Args) -> Ret {
    let playing = e.mem().read::<u8>(OPENING_PLAYING) != 0;
    if playing {
        let s = OPENING.fetch_or(O_WAITING, Ordering::AcqRel);
        let polls = OPENING_POLLS.fetch_add(1, Ordering::AcqRel) + 1;
        let timed_out = polls >= OPENING_LIMIT;
        if (FINISH.load(Ordering::Acquire) || timed_out) && s & O_FINISH_SENT == 0 {
            let player = MOVIES
                .lock()
                .ok()
                .and_then(|m| {
                    m.iter()
                        .rev()
                        .find(|x| x.name == OPENING_MOVIE)
                        .map(|x| x.player)
                })
                .unwrap_or(0);
            let _ = ffx_host::iggy::as3_call(e, player, "Finish", &[]);
            let mut bits = O_FINISH_SENT;
            if timed_out && !FINISH.load(Ordering::Acquire) {
                bits |= O_TIMED_OUT;
            }
            OPENING.fetch_or(bits, Ordering::AcqRel);
            ffx_host::log::note(&format!(
                "gd-ffx: the opening's Finish called (player {player:#x}, after {polls} waits{})",
                if bits & O_TIMED_OUT != 0 {
                    ", no frontend asked"
                } else {
                    ""
                }
            ));
        }
    } else if OPENING.load(Ordering::Acquire) & O_WAITING != 0 {
        OPENING.fetch_or(O_ENDED, Ordering::AcqRel);
    }
    let r = e
        .call_original(OPENING_PLAYING_FN, Conv::Cdecl, args)
        .unwrap_or(Ret::eax(0));
    if r.eax.trailing_zeros() >= 8 && OPENING.load(Ordering::Acquire) & O_WAITING != 0 {
        OPENING.fetch_or(O_ENDED, Ordering::AcqRel);
    }
    r
}

/// Asks the start-up loop's end (the native opening has played).
pub fn finish_opening() {
    FINISH.store(true, Ordering::Release);
}

/// [`O_WAITING`] and the other bits.
#[must_use]
pub fn opening_state() -> u32 {
    OPENING.load(Ordering::Acquire)
}

/// Notes the host's draws of each movie's player (call it once a frame:
/// a player's draws are lost when the game deletes it).
pub fn sample(iggy: &ffx_host::iggy::Iggy) {
    let players = iggy.players();
    if players.is_empty() {
        return;
    }
    let Ok(mut m) = MOVIES.lock() else { return };
    let draws: HashMap<u32, u64> = players.iter().map(|&(h, _, _, d)| (h, d)).collect();
    // the newest movie of a player handle owns it (handles are reused)
    let mut seen = std::collections::HashSet::new();
    for movie in m.iter_mut().rev() {
        if movie.player == 0 || !seen.insert(movie.player) {
            continue;
        }
        if let Some(&d) = draws.get(&movie.player)
            && d > movie.draws
        {
            if movie.draws == 0 {
                ffx_host::log::note(&format!(
                    "gd-ffx: flash movie {} shown: {}",
                    movie.seq, movie.name
                ));
            }
            movie.draws = d;
        }
    }
}

/// The movies opened so far.
#[must_use]
pub fn movies() -> Vec<Movie> {
    MOVIES.lock().map(|m| m.clone()).unwrap_or_default()
}

/// How many calls wait.
#[must_use]
pub fn pending() -> usize {
    CALLS.lock().map_or(0, |q| q.len())
}

/// The calls since the last time (oldest first), and how many were dropped
/// in all.
#[must_use]
pub fn take_calls() -> (Vec<Call>, u32) {
    let calls = CALLS
        .lock()
        .map(|mut q| q.drain(..).collect())
        .unwrap_or_default();
    (calls, DROPPED.load(Ordering::Relaxed))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn movie_names_from_paths() {
        assert_eq!(
            file_name("ffx_data/gamedata/ps3data/flash/OpeningScreen.swf"),
            "openingscreen.swf"
        );
        assert_eq!(file_name("C:\\x\\Box_kr.swf"), "box_kr.swf");
        assert_eq!(file_name("chocobo.swf"), "chocobo.swf");
    }
}
