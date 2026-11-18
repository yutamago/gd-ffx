//! The game's music, sound effects, voice and movies as it asks for them,
//! for replacement by id (R7; docs/architecture.md, "Sound", "Replacing
//! music, sound effects, voice and movies").
//!
//! The mix stays ffx-rust's (its FMOD engine); what the game asks of it is
//! seen at one place, `Fmod_PushCommand(mgr; cmd, a, b, c, d)`
//! (`0x006fa300`, wrapped in the function table as `crate::flash` wraps its
//! functions): every `iMusic*`, `iSfx*` and `iVoice*` call queues one
//! command there (ffx-rust's sound.md, "FMOD command queue"), from the
//! game's main thread or the loader. Each is noted for Godot
//! ([`take_audio`]) and, for an id Godot replaces ([`set_replaced`]), made
//! silent in the game's mix while the game still plays it (its timing,
//! play states and ends stay the game's):
//!
//! - **music** (`iMusicStart` 0x27, `CrossPlay` 0x17, `MultiPlay` 0x21,
//!   `PhrasePlay` 0x1b, `PlayFast` 0x18): the music volume (command 0x43,
//!   `FfxFmod +0x1c`, which the settings' music volume sets) is 0 while a
//!   replaced music is the one playing, and the settings' again when another
//!   starts or it stops;
//! - **voice** (`iVoiceRead(id)` 0x10, then `iVoiceStart(pan, volume)`
//!   0x0f): likewise the voice volume (0x44) while a replaced line plays;
//! - **sound effects** (`iSfxStart(id, volume, pan)` 0x33, `ForceStart`
//!   0x34, `SetVolume(id, volume)` 0x3b, `SetVolumeTranslate(id, volume,
//!   frames)` 0x3c): the effect's own volume is 0 (the commands' handlers:
//!   ffx-sound's ffxfmod.rs).
//!
//! Movies: `PVideoPlaybackWin32_open(player; path, loop)` (`0x00a27f20`)
//! names the movie, `PVideoPlaybackWin32_terminate(player)` (`0x00a28430`)
//! ends it, and the movie's sound (the FMOD user stream's read callback
//! `0x00a27ee0`, stdcall(sound, data, bytes)) is silenced while Godot plays
//! a replacement ([`set_movie_muted`]). Where the movie is (its frame,
//! paused, ended) is the manager's (`g_FMVPlayerManager`, [`movie_state`]).

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};

use ffx_bridge::Mem as _;
use ffx_host::exec::Exec;
use ffx_host::table::{Args, Conv, Entry, Port, Ret, Table};

/// `Fmod_PushCommand` (thiscall, 5 words): `push ebp; mov ebp, esp; push
/// ebx; push esi`.
const PUSH: u32 = 0x006f_a300;
const PUSH_PROLOGUE: &[u8] = &[0x55, 0x8b, 0xec, 0x53, 0x56];
/// `Sg_MainLoop(dt)` (cdecl, 1 word), once an update on the game's main
/// thread: `push ebp; mov ebp, esp; sub esp, 0xa4`. Where a change of the
/// replaced ids silences or restores the music and voice playing now.
const MAIN_LOOP: u32 = 0x0082_0ae0;
const MAIN_LOOP_PROLOGUE: &[u8] = &[0x55, 0x8b, 0xec, 0x81, 0xec, 0xa4, 0x00, 0x00, 0x00];
/// `g_FmodManager` (a pointer).
const FMOD_MANAGER: u32 = 0x00ce_9010;
/// `PVideoPlaybackWin32_open` (thiscall, 2 words): `push ebp; mov ebp, esp;
/// sub esp, 0x28`.
const OPEN: u32 = 0x00a2_7f20;
const OPEN_PROLOGUE: &[u8] = &[0x55, 0x8b, 0xec, 0x83, 0xec, 0x28];
/// `PVideoPlaybackWin32_terminate` (thiscall, 0 words): `push ebp; mov ebp,
/// esp; push ecx; push esi`.
const TERMINATE: u32 = 0x00a2_8430;
const TERMINATE_PROLOGUE: &[u8] = &[0x55, 0x8b, 0xec, 0x51, 0x56];
/// `PVideoPlaybackWin32_FmodReadCallback_00a27ee0` (stdcall, 3 words): `push
/// ebp; mov ebp, esp; push ecx; lea eax, [ebp - 4]`.
const READ: u32 = 0x00a2_7ee0;
const READ_PROLOGUE: &[u8] = &[0x55, 0x8b, 0xec, 0x51, 0x8d, 0x45, 0xfc];

/// `g_Settings` (a pointer): the PC settings; `+0x10` sound off, the music
/// volume at `+0x1c`, the voice volume at `+0x18` (0..100).
const SETTINGS: u32 = 0x022f_b504;
const SETTINGS_MUTE: u32 = 0x10;
/// `[0x00cded3c]`: `g_FMVPlayerManager`; `+0x6d0` playing, `+0x6d8` the
/// current frame (0xfffe ended or skipped), `+0x6fc` paused, `+0x710` ended.
const FMV_MANAGER: u32 = 0x00cd_ed3c;

/// The kinds of sound.
pub const MUSIC: u8 = 0;
pub const SFX: u8 = 1;
pub const VOICE: u8 = 2;
/// The settings' volumes (commands 0x42..0x45).
pub const SETTINGS_KIND: u8 = 3;

/// What happened.
pub const START: u8 = 1;
pub const STOP: u8 = 2;
pub const VOLUME: u8 = 3;
pub const FADE_OUT: u8 = 4;
pub const PAUSE: u8 = 5;
pub const STOP_ALL: u8 = 6;
pub const OTHER: u8 = 7;

/// One command the game queued, as Godot reads it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AudioEvent {
    pub kind: u8,
    pub action: u8,
    /// The command (FMOD's queue number).
    pub cmd: u32,
    /// The sound's id (music, effect, voice line; the volume's command for
    /// the settings).
    pub id: u32,
    /// The arguments as the command has them (volume 0..127, frames, pan).
    pub args: [u32; 3],
    /// Whether the game's own sound was made silent for it.
    pub replaced: bool,
}

/// The newest events kept for Godot (older ones dropped).
const MAX_EVENTS: usize = 4096;

#[derive(Default)]
struct Audio {
    events: VecDeque<AudioEvent>,
    replaced: [HashSet<u32>; 3],
    /// The voice line `iVoiceRead` asked for last.
    voice: Option<u32>,
    /// The music started last (`None` after a stop of all).
    music: Option<u32>,
    /// Whether the music / voice volume is held at 0 for a replacement.
    music_muted: bool,
    voice_muted: bool,
    /// Every id seen so far per kind, with how often it started.
    seen: [HashMap<u32, u32>; 3],
    /// The replaced ids changed: the music and voice holds are checked at
    /// the next update.
    pending: bool,
    /// The game's own volume for the music playing (0..127; iMusicVolume*).
    music_volume: u32,
}

fn audio() -> &'static Mutex<Audio> {
    static A: std::sync::OnceLock<Mutex<Audio>> = std::sync::OnceLock::new();
    A.get_or_init(|| Mutex::new(Audio::default()))
}

/// `FFX_AUDIO_LOG=1`: every command noted in the log.
fn log_commands() -> bool {
    static L: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *L.get_or_init(|| std::env::var("FFX_AUDIO_LOG").is_ok_and(|v| v == "1"))
}

/// The ids of `kind` Godot plays itself: the game's own are made silent.
pub fn set_replaced(kind: u8, ids: impl IntoIterator<Item = u32>) {
    if let Ok(mut a) = audio().lock()
        && let Some(set) = a.replaced.get_mut(usize::from(kind))
    {
        *set = ids.into_iter().collect();
        a.pending = true;
    }
}

/// The events since the last call.
#[must_use]
pub fn take_audio() -> Vec<AudioEvent> {
    audio()
        .lock()
        .map(|mut a| a.events.drain(..).collect())
        .unwrap_or_default()
}

/// Every id seen so far per kind (music, sound effects, voice) with its
/// start count.
#[must_use]
pub fn seen() -> [Vec<(u32, u32)>; 3] {
    let Ok(a) = audio().lock() else {
        return Default::default();
    };
    a.seen.clone().map(|m| {
        let mut v: Vec<(u32, u32)> = m.into_iter().collect();
        v.sort_unstable();
        v
    })
}

/// Wraps the functions above in the function table at the boot.
pub fn register(table: &mut Table) {
    for (addr, name, conv, words, port, prologue) in [
        (
            PUSH,
            "Fmod_PushCommand (gd-ffx: the sounds the game plays, replacements by id)",
            Conv::Thiscall,
            5,
            Port::Exec(push_command),
            PUSH_PROLOGUE,
        ),
        (
            MAIN_LOOP,
            "Sg_MainLoop (gd-ffx: the replaced sounds' holds after a change)",
            Conv::Cdecl,
            1,
            Port::Exec(main_loop),
            MAIN_LOOP_PROLOGUE,
        ),
        (
            OPEN,
            "PVideoPlaybackWin32_open (gd-ffx: the movie played)",
            Conv::Thiscall,
            2,
            Port::Exec(movie_open),
            OPEN_PROLOGUE,
        ),
        (
            TERMINATE,
            "PVideoPlaybackWin32_terminate (gd-ffx: the movie ended)",
            Conv::Thiscall,
            0,
            Port::Exec(movie_terminate),
            TERMINATE_PROLOGUE,
        ),
        (
            READ,
            "PVideoPlaybackWin32_FmodReadCallback (gd-ffx: a replaced movie's sound silenced)",
            Conv::Stdcall,
            3,
            Port::Exec(movie_read),
            READ_PROLOGUE,
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

/// Queues command `cmd` with arguments `a` through the original (the
/// manager in `ecx`).
fn push(e: &mut Exec, mgr: u32, cmd: u32, a: [u32; 4]) {
    if log_commands() {
        ffx_host::log::note(&format!(
            "gd-ffx: audio command {cmd:#04x} ({:#x}, {:#x}, {:#x}, {:#x}) pushed for a replacement",
            a[0], a[1], a[2], a[3]
        ));
    }
    let args = Args {
        ecx: mgr,
        edx: 0,
        stack: vec![cmd, a[0], a[1], a[2], a[3]],
        slots: 0,
    };
    let _ = e.call_original(PUSH, Conv::Thiscall, &args);
}

/// The settings' volume at `+off` (0..100; 0 while the sound is off).
fn setting(e: &Exec, off: u32) -> u32 {
    let mem = e.mem();
    let s: u32 = mem.read(SETTINGS);
    if s == 0 {
        return 100;
    }
    if mem.read::<u32>(s + SETTINGS_MUTE) != 0 {
        0
    } else {
        mem.read::<u32>(s + off).min(100)
    }
}

/// The music volume command and the settings' offset of its value.
const MUSIC_VOLUME: (u32, u32) = (0x43, 0x1c);
const VOICE_VOLUME: (u32, u32) = (0x44, 0x18);

// one match over the commands; a, b, c as the commands' arguments
#[allow(clippy::too_many_lines, clippy::many_single_char_names)]
fn push_command(e: &mut Exec, args: &Args) -> Ret {
    let mgr = args.ecx;
    let w = |i: usize| args.stack.get(i).copied().unwrap_or(0);
    let (cmd, a, b, c) = (w(0), w(1), w(2), w(3));
    let mut call = args.clone();
    // what the command is, and the volume holds to change around it
    let mut event: Option<AudioEvent> = None;
    let mut before: Vec<(u32, u32)> = Vec::new();
    let mut after: Vec<(u32, u32)> = Vec::new();
    if let Ok(mut s) = audio().lock() {
        let ev = |kind, action, id, args3: [u32; 3], replaced| AudioEvent {
            kind,
            action,
            cmd,
            id,
            args: args3,
            replaced,
        };
        match cmd {
            // music starts
            0x27 | 0x17 | 0x21 | 0x1b | 0x18 => {
                let replaced = s.replaced[0].contains(&a);
                *s.seen[0].entry(a).or_default() += 1;
                s.music = Some(a);
                s.music_volume = 0x7f;
                if replaced && !s.music_muted {
                    before.push((MUSIC_VOLUME.0, 0));
                    s.music_muted = true;
                } else if !replaced && s.music_muted {
                    before.push((MUSIC_VOLUME.0, u32::MAX));
                    s.music_muted = false;
                }
                if replaced {
                    // and the track's own volume (iMusicVolumeDirect(id, 0, 1)) after its start
                    after.push((0x23, a));
                }
                event = Some(ev(MUSIC, START, a, [b, c, 0], replaced));
            }
            // music stops: stop, clear, stop all, clear all
            0x26 | 0x25 | 0x2c | 0x2d => {
                let all = cmd == 0x2c || cmd == 0x2d;
                let id = if all { s.music.unwrap_or(u32::MAX) } else { a };
                let replaced = s.replaced[0].contains(&id);
                if (all || s.music == Some(a)) && s.music_muted {
                    after.push((MUSIC_VOLUME.0, u32::MAX));
                    s.music_muted = false;
                }
                if all || s.music == Some(a) {
                    s.music = None;
                }
                event = Some(ev(
                    MUSIC,
                    if all { STOP_ALL } else { STOP },
                    id,
                    [0; 3],
                    replaced,
                ));
            }
            0x2f => {
                event = Some(ev(
                    MUSIC,
                    PAUSE,
                    s.music.unwrap_or(u32::MAX),
                    [a, 0, 0],
                    false,
                ));
            }
            // iMusicVolumeDirect(id, v, 1), iMusicVolumeTranslate(id, frames, v)
            0x23 => {
                let replaced = s.replaced[0].contains(&a);
                if s.music == Some(a) {
                    s.music_volume = b.min(0x7f);
                }
                if replaced {
                    call.stack[2] = 0;
                }
                event = Some(ev(MUSIC, VOLUME, a, [b, 0, 0], replaced));
            }
            0x22 => {
                let replaced = s.replaced[0].contains(&a);
                if s.music == Some(a) {
                    s.music_volume = c.min(0x7f);
                }
                if replaced {
                    call.stack[3] = 0;
                }
                event = Some(ev(MUSIC, VOLUME, a, [c, b, 0], replaced));
            }
            0x1c => {
                let replaced = s.replaced[0].contains(&a);
                event = Some(ev(MUSIC, FADE_OUT, a, [b, 0, 0], replaced));
            }
            // sound effects
            0x33 | 0x34 => {
                let replaced = s.replaced[1].contains(&a);
                *s.seen[1].entry(a).or_default() += 1;
                if replaced {
                    call.stack[2] = 0; // its volume
                }
                event = Some(ev(SFX, START, a, [b, c, 0], replaced));
            }
            0x3b => {
                let replaced = s.replaced[1].contains(&a);
                if replaced {
                    call.stack[2] = 0;
                }
                event = Some(ev(SFX, VOLUME, a, [b, 0, 0], replaced));
            }
            // iSfxSetVolumeTranslate(id, volume, frames)
            0x3c => {
                let replaced = s.replaced[1].contains(&a);
                if replaced {
                    call.stack[2] = 0;
                }
                event = Some(ev(SFX, VOLUME, a, [b, c, 0], replaced));
            }
            0x3a => event = Some(ev(SFX, PAUSE, a, [b, 0, 0], s.replaced[1].contains(&a))),
            // stop (id), stop all non-blocking, stop non-blocking (id), stop all non-looping
            0x36 | 0x38 => event = Some(ev(SFX, STOP, a, [0; 3], s.replaced[1].contains(&a))),
            0x37 | 0x39 => event = Some(ev(SFX, STOP_ALL, u32::MAX, [0; 3], false)),
            0x30..=0x41 => {
                event = Some(ev(SFX, OTHER, a, [b, c, w(4)], s.replaced[1].contains(&a)));
            }
            // voice: the line read, then started
            0x10 => {
                s.voice = Some(a);
                event = Some(ev(VOICE, OTHER, a, [0; 3], s.replaced[2].contains(&a)));
            }
            0x0f => {
                let id = s.voice.unwrap_or(u32::MAX);
                let replaced = s.replaced[2].contains(&id);
                *s.seen[2].entry(id).or_default() += 1;
                if replaced && !s.voice_muted {
                    before.push((VOICE_VOLUME.0, 0));
                    s.voice_muted = true;
                } else if !replaced && s.voice_muted {
                    before.push((VOICE_VOLUME.0, u32::MAX));
                    s.voice_muted = false;
                }
                event = Some(ev(VOICE, START, id, [a, b, 0], replaced));
            }
            // iVoiceVolumeDirect(volume, 1), iVoiceVolumeTranslate(frames, volume), the voice stopped
            0x0e => {
                let id = s.voice.unwrap_or(u32::MAX);
                event = Some(ev(
                    VOICE,
                    VOLUME,
                    id,
                    [a, 0, 0],
                    s.replaced[2].contains(&id),
                ));
            }
            0x0d => {
                let id = s.voice.unwrap_or(u32::MAX);
                event = Some(ev(
                    VOICE,
                    VOLUME,
                    id,
                    [b, a, 0],
                    s.replaced[2].contains(&id),
                ));
            }
            0x0a => {
                let id = s.voice.unwrap_or(u32::MAX);
                event = Some(ev(VOICE, STOP, id, [0; 3], s.replaced[2].contains(&id)));
            }
            0x0b | 0x0c | 0x11..=0x13 => {
                let id = s.voice.unwrap_or(u32::MAX);
                event = Some(ev(VOICE, OTHER, id, [a, b, c], s.replaced[2].contains(&id)));
            }
            // the settings' volumes: master (also the movies'), music, voice, sound effects
            0x42..=0x45 => {
                if (cmd == MUSIC_VOLUME.0 && s.music_muted)
                    || (cmd == VOICE_VOLUME.0 && s.voice_muted)
                {
                    call.stack[1] = 0;
                }
                event = Some(ev(SETTINGS_KIND, VOLUME, cmd, [a, 0, 0], false));
            }
            _ => {}
        }
        if let Some(ev) = event {
            if s.events.len() >= MAX_EVENTS {
                s.events.pop_front();
            }
            s.events.push_back(ev);
        }
    }
    if log_commands() {
        ffx_host::log::note(&format!(
            "gd-ffx: audio command {cmd:#04x} ({a:#x}, {b:#x}, {c:#x}, {:#x}){}",
            w(4),
            if event.is_some_and(|e| e.replaced) {
                " replaced"
            } else {
                ""
            }
        ));
    }
    let resolve = |e: &Exec, (cmd, v): (u32, u32)| {
        if v != u32::MAX {
            return v;
        }
        let off = if cmd == MUSIC_VOLUME.0 {
            MUSIC_VOLUME.1
        } else {
            VOICE_VOLUME.1
        };
        setting(e, off)
    };
    for hold in before {
        let v = resolve(e, hold);
        push(e, mgr, hold.0, [v, 0, 0, 0]);
    }
    let ret = e
        .call_original(PUSH, Conv::Thiscall, &call)
        .unwrap_or(Ret::eax(0));
    for hold in after {
        if hold.0 == 0x23 {
            // a replaced track's own volume: 0
            push(e, mgr, 0x23, [hold.1, 0, 1, 0]);
            continue;
        }
        let v = resolve(e, hold);
        push(e, mgr, hold.0, [v, 0, 0, 0]);
    }
    ret
}

/// Each update: after a change of the replaced ids, the music or voice line
/// playing is silenced (now replaced) or given the settings' volume again
/// (no longer replaced, e.g. replacements switched off).
fn main_loop(e: &mut Exec, args: &Args) -> Ret {
    let mut holds: Vec<(u32, u32)> = Vec::new();
    let mut track: Option<(u32, u32)> = None;
    if let Ok(mut s) = audio().lock()
        && s.pending
    {
        s.pending = false;
        let music = s.music.is_some_and(|m| s.replaced[0].contains(&m));
        if music != s.music_muted {
            holds.push((MUSIC_VOLUME.0, if music { 0 } else { u32::MAX }));
            s.music_muted = music;
            // the track's own volume: 0, or the game's again
            if let Some(m) = s.music {
                track = Some((m, if music { 0 } else { s.music_volume }));
            }
        }
        let voice = s.voice.is_some_and(|v| s.replaced[2].contains(&v));
        if voice != s.voice_muted {
            holds.push((VOICE_VOLUME.0, if voice { 0 } else { u32::MAX }));
            s.voice_muted = voice;
        }
    }
    if !holds.is_empty() {
        let mgr: u32 = e.mem().read(FMOD_MANAGER);
        if mgr != 0 {
            for (cmd, v) in holds {
                let v = if v == u32::MAX {
                    setting(
                        e,
                        if cmd == MUSIC_VOLUME.0 {
                            MUSIC_VOLUME.1
                        } else {
                            VOICE_VOLUME.1
                        },
                    )
                } else {
                    v
                };
                push(e, mgr, cmd, [v, 0, 0, 0]);
            }
            if let Some((id, v)) = track {
                push(e, mgr, 0x23, [id, v, 1, 0]);
            }
        }
    }
    e.call_original(MAIN_LOOP, Conv::Cdecl, args)
        .unwrap_or(Ret::eax(0))
}

/// The game's FMOD volumes now (`FfxFmod +0x18..+0x24`: master, music,
/// voice, sound effects, 0..1; the music and voice ones are 0 while a
/// replacement holds them), and whether the music and voice are held.
#[must_use]
pub fn fmod_volumes(shared: &ffx_host::exec::Shared) -> ([f32; 4], bool, bool) {
    let arena = shared.arena();
    let read = |at: u32| -> u32 {
        if at == 0 || !arena.is_mapped(at, 4) {
            return 0;
        }
        ffx_host::arena::ArenaMem {
            arena: std::sync::Arc::clone(arena),
        }
        .read(at)
    };
    let mgr = read(FMOD_MANAGER);
    let fmod = if mgr == 0 { 0 } else { read(mgr + 8) };
    let v = if fmod == 0 {
        [0.0; 4]
    } else {
        [0x18, 0x1c, 0x20, 0x24].map(|o| f32::from_bits(read(fmod + o)))
    };
    let (m, vo) = audio()
        .lock()
        .map(|a| (a.music_muted, a.voice_muted))
        .unwrap_or_default();
    (v, m, vo)
}

/// A movie the game opened.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MovieEvent {
    /// 1 opened, 2 terminated.
    pub action: u8,
    /// The movie's file name (`IWK_us.webm`; empty on terminate).
    pub name: String,
    /// The path as the game gave it.
    pub path: String,
    pub player: u32,
    pub looping: bool,
}

fn movies() -> &'static Mutex<VecDeque<MovieEvent>> {
    static M: std::sync::OnceLock<Mutex<VecDeque<MovieEvent>>> = std::sync::OnceLock::new();
    M.get_or_init(|| Mutex::new(VecDeque::new()))
}

static MOVIE_MUTED: AtomicBool = AtomicBool::new(false);
/// The player whose movie is the manager's (the last opened one with sound).
static MOVIE_PLAYER: AtomicU32 = AtomicU32::new(0);

/// Silences the movie's own sound while Godot plays a replacement.
pub fn set_movie_muted(on: bool) {
    MOVIE_MUTED.store(on, Ordering::Release);
}

/// The movie events since the last call.
#[must_use]
pub fn take_movies() -> Vec<MovieEvent> {
    movies()
        .lock()
        .map(|mut m| m.drain(..).collect())
        .unwrap_or_default()
}

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

fn note_movie(ev: MovieEvent) {
    ffx_host::log::note(&format!(
        "gd-ffx: movie {} {} (player {:#x}{})",
        if ev.action == 1 {
            "opened"
        } else {
            "terminated"
        },
        if ev.path.is_empty() {
            "-"
        } else {
            ev.path.as_str()
        },
        ev.player,
        if ev.looping { ", looping" } else { "" }
    ));
    if let Ok(mut m) = movies().lock() {
        if m.len() >= 256 {
            m.pop_front();
        }
        m.push_back(ev);
    }
}

fn movie_open(e: &mut Exec, args: &Args) -> Ret {
    let path = c_string(e, args.stack.first().copied().unwrap_or(0), 512);
    let looping = args.stack.get(1).copied().unwrap_or(0) != 0;
    let ret = e
        .call_original(OPEN, Conv::Thiscall, args)
        .unwrap_or(Ret::eax(u32::MAX));
    let name = path.rsplit(['/', '\\']).next().unwrap_or(&path).to_string();
    if !name.to_ascii_lowercase().contains("_480") {
        MOVIE_PLAYER.store(args.ecx, Ordering::Release);
    }
    note_movie(MovieEvent {
        action: 1,
        name,
        path,
        player: args.ecx,
        looping,
    });
    ret
}

fn movie_terminate(e: &mut Exec, args: &Args) -> Ret {
    let ret = e
        .call_original(TERMINATE, Conv::Thiscall, args)
        .unwrap_or(Ret::eax(0));
    note_movie(MovieEvent {
        action: 2,
        name: String::new(),
        path: String::new(),
        player: args.ecx,
        looping: false,
    });
    ret
}

fn movie_read(e: &mut Exec, args: &Args) -> Ret {
    let ret = e
        .call_original(READ, Conv::Stdcall, args)
        .unwrap_or(Ret::eax(0));
    if MOVIE_MUTED.load(Ordering::Acquire) {
        let (data, len) = (
            args.stack.get(1).copied().unwrap_or(0),
            args.stack.get(2).copied().unwrap_or(0),
        );
        if data != 0 && len > 0 && len <= 1 << 20 {
            e.mem().write_bytes(data, &vec![0u8; len as usize]);
        }
    }
    ret
}

/// Where the manager's movie is.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct MovieState {
    pub playing: bool,
    /// The frame shown (29.97 a second; 0xfffe ended or skipped).
    pub frame: u32,
    pub paused: bool,
    pub ended: bool,
    /// The player that plays it.
    pub player: u32,
}

/// The manager's movie now (read from the game's memory).
#[must_use]
pub fn movie_state(shared: &ffx_host::exec::Shared) -> MovieState {
    let arena = shared.arena();
    let read_u32 = |at: u32| -> u32 {
        if at == 0 || !arena.is_mapped(at, 4) {
            return 0;
        }
        ffx_host::arena::ArenaMem {
            arena: std::sync::Arc::clone(arena),
        }
        .read(at)
    };
    let read_u8 = |at: u32| -> u8 {
        if at == 0 || !arena.is_mapped(at, 1) {
            return 0;
        }
        ffx_host::arena::ArenaMem {
            arena: std::sync::Arc::clone(arena),
        }
        .read(at)
    };
    let m = read_u32(FMV_MANAGER);
    if m == 0 {
        return MovieState::default();
    }
    MovieState {
        playing: read_u8(m + 0x6d0) != 0,
        frame: read_u32(m + 0x6d8),
        paused: read_u8(m + 0x6fc) != 0,
        ended: read_u8(m + 0x710) != 0,
        player: MOVIE_PLAYER.load(Ordering::Acquire),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replaced_ids_are_kept_per_kind() {
        set_replaced(SFX, [5, 7]);
        set_replaced(MUSIC, [0x10]);
        let a = audio().lock().unwrap();
        assert!(a.replaced[1].contains(&7));
        assert!(a.replaced[0].contains(&0x10));
        assert!(!a.replaced[2].contains(&5));
    }
}
