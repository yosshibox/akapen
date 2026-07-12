//! Palm rejection — keep a resting hand off the drawing path (spec §5.2).
//!
//! When you draw on a display tablet or a Surface, the hand holding the pen
//! rests on the glass and its touches must **not** turn into strokes (or even
//! pan the canvas out from under the pen). This is a **pure, OS-neutral little
//! state machine** — the same shape as [`crate::keymap`]: the shells (mac
//! NSEvent, later Windows WM_POINTER / Wintab) classify each raw event into a
//! [`PointerKind`] and hand it here with a timestamp; we answer where it should
//! go ([`Routing`]) and update the pen-priority lock. Keeping the judgment in
//! the core means every platform shares one contract and it is unit-testable
//! without a GUI or a tablet (the interactive/liquid-tablet check is the §5.6
//! release gate, verified separately on real hardware).
//!
//! The rule (spec §5.2):
//! - **Pen and mouse always draw.** A pen is the intended input; a mouse is the
//!   desktop fallback and is never a palm.
//! - **Touch during pen contact is a palm → ignore it entirely** (not even a
//!   pan): the hand is resting while the pen is down.
//! - **Pen-priority lock:** for a short window *after* the pen lifts, touches
//!   are still treated as palm and ignored. A palm often stays on the glass a
//!   fraction of a second after the pen tip leaves it; without this window that
//!   trailing palm contact would suddenly pan the canvas.
//! - **Touch with no active pen** is a deliberate gesture → route it to canvas
//!   pan/pinch ([`Routing::Navigate`]), never to drawing. (The touch-only
//!   "finger draw" mode of spec §5.2 is a later, explicit opt-in; MVP keeps
//!   touch off the ink path — matching the core engine, which ignores `Touch`
//!   samples in `engine::Engine::push_pointer`.)
//!
//! Time is **injected** (`now_ms`), never read from a clock here, so the lock
//! boundaries are fixable in tests. The lock duration is injectable too
//! ([`route_with_lock`]); [`route`] uses the [`PEN_PRIORITY_LOCK_MS`] default.

use crate::engine::Phase;
use crate::stroke::PointerKind;

/// Default pen-priority lock, in milliseconds: how long after the pen lifts a
/// touch is still rejected as palm (spec §5.2 "最後にペンを見てから一定時間";
/// the spec fixes no number). 500 ms is a deliberate middle ground — long
/// enough to bridge the gap where the palm is still resting just after the pen
/// tip leaves the glass (the trailing-palm pan we must not fire), short enough
/// that a two-finger pan the user *deliberately* starts right after a stroke
/// isn't left feeling stuck. It matches the few-hundred-ms window typical of
/// pen-first apps (CSP-class palm rejection). The real value is confirmed at
/// the §5.6 liquid-tablet gate; this is the safe-side default until then.
pub const PEN_PRIORITY_LOCK_MS: i64 = 500;

/// Where a classified pointer event should go.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Routing {
    /// Feed it to the drawing engine (pen, or a mouse fallback).
    Draw,
    /// Use it for canvas navigation (pan/pinch), **not** drawing — a deliberate
    /// touch with no pen in play.
    Navigate,
    /// Drop it entirely: a palm touch during pen contact or within the
    /// pen-priority lock window.
    Ignore,
}

/// The tiny palm-rejection state carried between events. `Default`/[`new`] is
/// "no pen seen yet": nothing down, no lock.
///
/// [`new`]: PalmState::new
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PalmState {
    /// A pen is currently in contact (between its `Down` and `Up`).
    pen_down: bool,
    /// Absolute time (ms) until which touches are rejected as palm, armed when
    /// the pen lifts. `None` = no active post-pen lock.
    lock_until_ms: Option<i64>,
}

impl PalmState {
    /// A fresh state: no pen down, no lock.
    pub const fn new() -> Self {
        PalmState {
            pen_down: false,
            lock_until_ms: None,
        }
    }

    /// Rebuilds a state from the flat `(pen_down, lock_until_ms)` pair the C ABI
    /// marshals across the boundary (`lock_until_ms == None` means no lock).
    pub const fn from_parts(pen_down: bool, lock_until_ms: Option<i64>) -> Self {
        PalmState {
            pen_down,
            lock_until_ms,
        }
    }

    /// Decomposes into that flat pair for the C ABI to store back.
    pub const fn into_parts(self) -> (bool, Option<i64>) {
        (self.pen_down, self.lock_until_ms)
    }

    /// Whether the pen-priority lock is still active at `now_ms`. The boundary
    /// is exclusive: at exactly `lock_until_ms` the lock has expired.
    fn locked_at(&self, now_ms: i64) -> bool {
        match self.lock_until_ms {
            Some(until) => now_ms < until,
            None => false,
        }
    }
}

/// Routes one classified pointer event, updating `state`, using the default
/// [`PEN_PRIORITY_LOCK_MS`] lock. Pure (time is injected via `now_ms`).
pub fn route(state: &mut PalmState, kind: PointerKind, phase: Phase, now_ms: i64) -> Routing {
    route_with_lock(state, kind, phase, now_ms, PEN_PRIORITY_LOCK_MS)
}

/// Like [`route`], but the pen-priority lock duration is injected (`lock_ms`) so
/// tests can fix the exact boundary. This is the actual state machine.
pub fn route_with_lock(
    state: &mut PalmState,
    kind: PointerKind,
    phase: Phase,
    now_ms: i64,
    lock_ms: i64,
) -> Routing {
    match kind {
        PointerKind::Pen => {
            match phase {
                // Pen in contact: mark it down and clear any stale lock (a fresh
                // pen contact supersedes the previous stroke's release window).
                Phase::Down | Phase::Move => {
                    state.pen_down = true;
                    state.lock_until_ms = None;
                }
                // Pen lifted: arm the pen-priority lock so a trailing palm can't
                // immediately pan.
                Phase::Up => {
                    state.pen_down = false;
                    state.lock_until_ms = Some(now_ms + lock_ms);
                }
            }
            Routing::Draw
        }
        // A mouse is the desktop fallback and never a palm — always draws, and
        // is unaffected by the pen lock.
        PointerKind::Mouse => Routing::Draw,
        // Touch: palm while a pen is down or the lock is live; otherwise a
        // deliberate navigation gesture. Never drawing in MVP.
        PointerKind::Touch => {
            if state.pen_down || state.locked_at(now_ms) {
                Routing::Ignore
            } else {
                Routing::Navigate
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Small builders keep the cases readable (the spec §5.2 rules are the
    // source of truth; this table *is* the fixed contract, like keymap's).
    fn pen(state: &mut PalmState, phase: Phase, now_ms: i64) -> Routing {
        route(state, PointerKind::Pen, phase, now_ms)
    }
    fn touch(state: &mut PalmState, phase: Phase, now_ms: i64) -> Routing {
        route(state, PointerKind::Touch, phase, now_ms)
    }
    fn mouse(state: &mut PalmState, phase: Phase, now_ms: i64) -> Routing {
        route(state, PointerKind::Mouse, phase, now_ms)
    }

    #[test]
    fn touch_alone_navigates_not_draws() {
        // No pen ever seen: a touch is a deliberate pan/pinch, never ink.
        let mut s = PalmState::new();
        assert_eq!(touch(&mut s, Phase::Down, 0), Routing::Navigate);
        assert_eq!(touch(&mut s, Phase::Move, 5), Routing::Navigate);
        assert_eq!(touch(&mut s, Phase::Up, 10), Routing::Navigate);
    }

    #[test]
    fn touch_during_pen_contact_is_ignored_as_palm() {
        let mut s = PalmState::new();
        assert_eq!(pen(&mut s, Phase::Down, 0), Routing::Draw);
        // Palm lands while the pen is down → dropped entirely (not even a pan).
        assert_eq!(touch(&mut s, Phase::Down, 1), Routing::Ignore);
        assert_eq!(touch(&mut s, Phase::Move, 2), Routing::Ignore);
        assert_eq!(touch(&mut s, Phase::Up, 3), Routing::Ignore);
        // Pen keeps drawing through it all.
        assert_eq!(pen(&mut s, Phase::Move, 4), Routing::Draw);
    }

    #[test]
    fn touch_within_pen_priority_lock_after_up_is_ignored() {
        let mut s = PalmState::new();
        pen(&mut s, Phase::Down, 100);
        assert_eq!(pen(&mut s, Phase::Up, 200), Routing::Draw); // lock until 700
                                                                // Trailing palm just after the pen lifts, well inside the window.
        assert_eq!(touch(&mut s, Phase::Down, 250), Routing::Ignore);
        assert_eq!(touch(&mut s, Phase::Move, 400), Routing::Ignore);
    }

    #[test]
    fn touch_after_lock_expiry_navigates_again() {
        let mut s = PalmState::new();
        pen(&mut s, Phase::Down, 100);
        pen(&mut s, Phase::Up, 200); // default lock 500 → until 700
                                     // Past the window: a deliberate touch pans again.
        assert_eq!(touch(&mut s, Phase::Down, 900), Routing::Navigate);
    }

    #[test]
    fn lock_boundary_is_exclusive_at_expiry() {
        // Fix the boundary with an injected lock duration.
        let lock_ms = 500;
        let mut s = PalmState::new();
        route_with_lock(&mut s, PointerKind::Pen, Phase::Down, 1000, lock_ms);
        route_with_lock(&mut s, PointerKind::Pen, Phase::Up, 1000, lock_ms); // until 1500
                                                                             // 1 ms before expiry: still palm.
        assert_eq!(
            route_with_lock(&mut s, PointerKind::Touch, Phase::Down, 1499, lock_ms),
            Routing::Ignore,
            "just inside the lock"
        );
        // Exactly at expiry: lock is over (exclusive boundary).
        assert_eq!(
            route_with_lock(&mut s, PointerKind::Touch, Phase::Down, 1500, lock_ms),
            Routing::Navigate,
            "at the boundary the lock has expired"
        );
        // After expiry: navigation.
        assert_eq!(
            route_with_lock(&mut s, PointerKind::Touch, Phase::Down, 1501, lock_ms),
            Routing::Navigate,
        );
    }

    #[test]
    fn mouse_always_draws_even_during_pen_lock() {
        let mut s = PalmState::new();
        // Mouse alone draws.
        assert_eq!(mouse(&mut s, Phase::Down, 0), Routing::Draw);
        // A pen stroke arms the lock...
        pen(&mut s, Phase::Down, 100);
        pen(&mut s, Phase::Up, 200);
        // ...but a mouse is never a palm: it still draws inside the window.
        assert_eq!(mouse(&mut s, Phase::Down, 250), Routing::Draw);
        // And a mouse never arms or clears the lock, so a touch is still gated.
        assert_eq!(touch(&mut s, Phase::Down, 300), Routing::Ignore);
    }

    #[test]
    fn pen_down_move_up_all_draw_and_track_state() {
        let mut s = PalmState::new();
        assert_eq!(pen(&mut s, Phase::Down, 0), Routing::Draw);
        assert!(s.into_parts().0, "pen down tracked");
        assert_eq!(pen(&mut s, Phase::Move, 1), Routing::Draw);
        assert!(s.into_parts().0, "still down on move");
        assert_eq!(pen(&mut s, Phase::Up, 2), Routing::Draw);
        let (down, lock) = s.into_parts();
        assert!(!down, "pen released");
        assert_eq!(
            lock,
            Some(2 + PEN_PRIORITY_LOCK_MS),
            "lock armed at up + default"
        );
    }

    #[test]
    fn a_new_pen_contact_supersedes_a_prior_release_lock() {
        let mut s = PalmState::new();
        pen(&mut s, Phase::Down, 0);
        pen(&mut s, Phase::Up, 100); // lock armed
        assert!(s.into_parts().1.is_some(), "lock present after up");
        // Starting a fresh stroke clears the stale release lock.
        pen(&mut s, Phase::Down, 120);
        assert_eq!(s.into_parts().1, None, "new pen down clears the lock");
        // ...and touches are now gated by pen contact, not the (cleared) lock.
        assert_eq!(touch(&mut s, Phase::Down, 121), Routing::Ignore);
    }

    #[test]
    fn default_route_uses_the_pen_priority_lock_constant() {
        let mut a = PalmState::new();
        let mut b = PalmState::new();
        // route() must behave exactly like route_with_lock(.., PEN_PRIORITY_LOCK_MS).
        route(&mut a, PointerKind::Pen, Phase::Up, 0);
        route_with_lock(&mut b, PointerKind::Pen, Phase::Up, 0, PEN_PRIORITY_LOCK_MS);
        assert_eq!(a, b);
        assert_eq!(a.into_parts().1, Some(PEN_PRIORITY_LOCK_MS));
    }
}
