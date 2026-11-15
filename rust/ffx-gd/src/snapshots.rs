//! The game-state API's snapshots on Godot's side: ffx-rust hands one to
//! the observer after every update (on the game's main thread,
//! `ffx_host::observe`); the newest two are kept here with the time the
//! newest came, and Godot's frames interpolate between them (C#:
//! `GdFfx.GameStateView`). Rendering runs one update behind the game, as
//! interpolation needs.

use std::sync::{Arc, Mutex, Once, OnceLock};
use std::time::Instant;

use ffx_state::Snapshot;

/// The game's pace: 30 updates a second.
pub const UPDATE: f64 = 1.0 / 30.0;

#[derive(Default)]
struct Pair {
    previous: Option<Arc<Snapshot>>,
    current: Option<Arc<Snapshot>>,
    /// When `current` came.
    at: Option<Instant>,
    /// The time between the last two (smoothed), seconds.
    interval: f64,
    /// `current` encoded, made on the first ask.
    current_bytes: Option<Arc<Vec<u8>>>,
    previous_bytes: Option<Arc<Vec<u8>>>,
}

/// The newest two snapshots.
#[derive(Default)]
pub struct Snapshots {
    pair: Mutex<Pair>,
}

impl Snapshots {
    /// A new snapshot came (the game's main thread: only stores it).
    pub fn push(&self, s: &Arc<Snapshot>) {
        let now = Instant::now();
        let Ok(mut p) = self.pair.lock() else { return };
        if let Some(at) = p.at {
            let dt = now.duration_since(at).as_secs_f64().clamp(0.001, 0.5);
            p.interval = if p.interval == 0.0 {
                dt
            } else {
                p.interval * 0.9 + dt * 0.1
            };
        }
        p.previous = p.current.take();
        p.previous_bytes = p.current_bytes.take();
        p.current = Some(Arc::clone(s));
        p.at = Some(now);
    }

    /// The newest update number, or -1 before the first snapshot.
    #[must_use]
    pub fn update(&self) -> i64 {
        self.pair
            .lock()
            .ok()
            .and_then(|p| p.current.as_ref().map(|s| i64::from(s.update)))
            .unwrap_or(-1)
    }

    /// The newest snapshot.
    #[must_use]
    pub fn current(&self) -> Option<Arc<Snapshot>> {
        self.pair.lock().ok().and_then(|p| p.current.clone())
    }

    /// (previous, current) encoded, seconds since the current came, the
    /// interval between updates.
    #[must_use]
    pub fn encoded(&self) -> (Option<Arc<Vec<u8>>>, Option<Arc<Vec<u8>>>, f64, f64) {
        let Ok(mut p) = self.pair.lock() else {
            return (None, None, 0.0, UPDATE);
        };
        if p.current_bytes.is_none() {
            p.current_bytes = p.current.as_ref().map(|s| Arc::new(s.encode()));
        }
        if p.previous_bytes.is_none() {
            p.previous_bytes = p.previous.as_ref().map(|s| Arc::new(s.encode()));
        }
        let age = p.at.map_or(0.0, |t| t.elapsed().as_secs_f64());
        let interval = if p.interval > 0.0 { p.interval } else { UPDATE };
        (
            p.previous_bytes.clone(),
            p.current_bytes.clone(),
            age,
            interval,
        )
    }
}

/// The process's snapshots (one game per process).
pub fn global() -> &'static Arc<Snapshots> {
    static S: OnceLock<Arc<Snapshots>> = OnceLock::new();
    S.get_or_init(Arc::default)
}

/// Registers the observer that feeds [`global`] (once).
pub fn observe() {
    static ONCE: Once = Once::new();
    ONCE.call_once(|| {
        let s = Arc::clone(global());
        ffx_host::observe::observe(Box::new(move |snap| s.push(snap)));
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_newest_two_with_their_encoding() {
        let s = Snapshots::default();
        assert_eq!(s.update(), -1);
        for u in 1..=3 {
            s.push(&Arc::new(Snapshot {
                update: u,
                ..Snapshot::default()
            }));
        }
        assert_eq!(s.update(), 3);
        let (prev, cur, age, interval) = s.encoded();
        let (prev, cur) = (prev.unwrap(), cur.unwrap());
        assert_eq!(u32::from_le_bytes(prev[8..12].try_into().unwrap()), 2);
        assert_eq!(u32::from_le_bytes(cur[8..12].try_into().unwrap()), 3);
        assert!(age >= 0.0 && interval > 0.0);
    }
}
