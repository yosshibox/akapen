//! Keyboard key -> editor action mapping (spec §3).
//!
//! This is a **pure, OS-neutral, mode-independent** function: given a described
//! key event (produced character, physical key identity, modifier state and the
//! IME / text-editing guards) it returns the intended [`Action`], or `None` when
//! the key should be left alone (unknown key, or the shell is in text
//! input / IME composition and must not steal keys — the B15/B21 lesson from the
//! VEDA reference `lib/annotate-keys.js`).
//!
//! Design (spec §3, cross-platform rule 5):
//! - **Character-first, physical-fallback.** The shortcut table is written in
//!   terms of the characters CLIP STUDIO uses (`P`, `[`, `-`, `^` …). We match
//!   the produced character first, then fall back to the *physical* key
//!   (keyCode / scancode) so array-dependent keys (`[` `]` `-` `^`) and keys an
//!   IME/layout would otherwise swallow still work. This mirrors the reference
//!   implementation's `normalizeKey` + physical-key recovery.
//! - **Mode-independent.** Akapen is a markup-only app (spec §3.2), so a key's
//!   meaning never depends on a "playback vs annotate" mode. There is exactly
//!   one mapping.
//! - **No OS types.** Inputs are plain data ([`KeyInput`]); the shells (mac
//!   NSEvent, Windows WM_KEYDOWN) translate their native events into it. Keeps
//!   the mapping identical on every platform and unit-testable without a GUI.
//!
//! Hold-gestures (plain **Space** = momentary pan, **Shift+Space drag** = canvas
//! rotate) are *not* discrete actions and are intentionally absent here: they are
//! modal states the shell tracks directly. This module only maps discrete
//! key-down actions.

use crate::stroke::Tool;

/// Modifier state, described OS-neutrally.
///
/// `primary` is the platform's primary accelerator modifier — **Command** on
/// macOS, **Control** on Windows/Linux (cross-platform rule 1). The shell folds
/// its native Cmd/Ctrl into this single flag so the mapping is identical
/// everywhere.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Modifiers {
    pub primary: bool,
    pub shift: bool,
    pub alt: bool,
}

impl Modifiers {
    pub const NONE: Modifiers = Modifiers {
        primary: false,
        shift: false,
        alt: false,
    };
    pub const PRIMARY: Modifiers = Modifiers {
        primary: true,
        shift: false,
        alt: false,
    };
}

/// An OS-neutral physical key identity (keyCode / scancode position), used as
/// the array-independent fallback when the produced character does not resolve
/// (different keyboard layout, or an IME/layout swallowed the character).
///
/// Only the keys the spec §3 table needs are enumerated; everything else is
/// [`PhysicalKey::Other`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PhysicalKey {
    KeyP,
    KeyE,
    KeyU,
    KeyA,
    KeyR,
    KeyO,
    KeyT,
    KeyI,
    KeyX,
    KeyC,
    KeyZ,
    KeyY,
    Digit0,
    Space,
    BracketLeft,
    BracketRight,
    /// The `-` key (US: to the right of `0`; JIS: same position).
    Minus,
    /// The JIS `^` key (US layout has no dedicated caret key).
    Caret,
    PageUp,
    PageDown,
    /// The `B` key (Photoshop preset: Brush → Pen tool).
    KeyB,
    /// The `1` digit key (Photoshop preset: Ctrl+1 → 100% view).
    Digit1,
    ArrowLeft,
    ArrowRight,
    ArrowUp,
    ArrowDown,
    Other,
}

/// A named shortcut table. The key → action mapping stays a pure function; the
/// preset only selects which table the same [`KeyInput`] is resolved against.
///
/// - [`KeymapPreset::Photoshop`] (the product default since V1.1): every
///   shortcut that exists in Adobe Photoshop's default set is copied verbatim
///   (`B` brush, `Ctrl+Shift+Z` redo, `Ctrl+Alt+Z` step backward, `Ctrl+1`
///   100%, `Ctrl+Y` deliberately unmapped because Photoshop uses it for proof
///   colors). Akapen-only features with no Photoshop equivalent (shape tools,
///   discrete rotate, transparent color, frame stepping) keep their spec §3
///   keys, which collide with nothing in the Photoshop set.
/// - [`KeymapPreset::ClipStudio`] is the original spec §3 CSP-compatible table.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum KeymapPreset {
    #[default]
    Photoshop,
    ClipStudio,
}

/// A described key-down event: the produced character (if any), the physical
/// key, the modifier state, and the two "hands off" guards.
#[derive(Debug, Clone, Copy)]
pub struct KeyInput {
    /// The character the event produced *ignoring* the primary/alt modifiers
    /// (e.g. mac `charactersIgnoringModifiers`), or `None` if it produced none.
    pub ch: Option<char>,
    pub physical: PhysicalKey,
    pub mods: Modifiers,
    /// An IME composition is in progress (marked text). Never steal keys — the
    /// B21 lesson (an IME confirm Enter must not be hijacked).
    pub composing: bool,
    /// Focus is in a text-editing control (text field / field editor). Let it
    /// keep the keys — the B15 lesson (fixed-layout keys like `[` must not fire
    /// a tool switch while someone is typing).
    pub text_editing: bool,
}

impl KeyInput {
    /// Convenience constructor for a bare (no-modifier, not-composing,
    /// not-in-a-text-field) character key. Mainly for tests and simple shells.
    pub fn ch(c: char, physical: PhysicalKey) -> Self {
        KeyInput {
            ch: Some(c),
            physical,
            mods: Modifiers::NONE,
            composing: false,
            text_editing: false,
        }
    }
}

/// A discrete editor action produced by a key. The `SelectTool` payload and the
/// M3 color actions are present so the mapping is complete now; the M1 shell only
/// acts on the MVP subset (Pen/Eraser, undo/redo, zoom/fit/100%, rotate, brush
/// size, frame nav) and ignores the rest.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Action {
    SelectTool(Tool),
    Undo,
    Redo,
    ZoomIn,
    ZoomOut,
    FitToWindow,
    ActualSize,
    RotateLeft,
    RotateRight,
    BrushSmaller,
    BrushLarger,
    NextFrame,
    PrevFrame,
    // ── M3 (mapped now for completeness; not acted on by the M1 shell) ──
    SwapColor,
    Eyedropper,
    TransparentColor,
}

/// Resolves a described key event against the CLIP STUDIO preset (spec §3).
/// Kept as the stable entry point for existing callers; new callers should use
/// [`resolve_preset`].
pub fn resolve(input: KeyInput) -> Option<Action> {
    resolve_preset(KeymapPreset::ClipStudio, input)
}

/// Resolves a described key event to an [`Action`] under the given preset, or
/// `None` to leave the key alone. Pure and mode-independent (spec §3).
pub fn resolve_preset(preset: KeymapPreset, input: KeyInput) -> Option<Action> {
    // Guards first (spec §3, B15/B21): never steal keys while the user is typing
    // or an IME is composing.
    if input.composing || input.text_editing {
        return None;
    }
    // Character-first, then array-independent physical fallback.
    match preset {
        KeymapPreset::ClipStudio => resolve_char(input.ch, input.mods)
            .or_else(|| resolve_physical(input.physical, input.mods)),
        KeymapPreset::Photoshop => resolve_char_ps(input.ch, input.mods)
            .or_else(|| resolve_physical_ps(input.physical, input.mods)),
    }
}

/// Arrow keys are shared by both presets and physical-only (arrows produce no
/// character): Left/Right step the frame sequence, Up/Down zoom. Frame stepping
/// on ←/→ matches the Windows product behavior recorded in the V1.0 baseline
/// document; ↑/↓ zoom is the V1.1 addition.
fn resolve_arrows(key: PhysicalKey, m: Modifiers) -> Option<Action> {
    if m.primary || m.alt {
        return None;
    }
    match key {
        PhysicalKey::ArrowLeft => Some(Action::PrevFrame),
        PhysicalKey::ArrowRight => Some(Action::NextFrame),
        PhysicalKey::ArrowUp => Some(Action::ZoomIn),
        PhysicalKey::ArrowDown => Some(Action::ZoomOut),
        _ => None,
    }
}

fn resolve_char(ch: Option<char>, m: Modifiers) -> Option<Action> {
    let ch = ch?;
    let lower = ch.to_ascii_lowercase();

    // Primary-accelerator combinations (Cmd/Ctrl + key).
    if m.primary {
        return match lower {
            'z' => Some(if m.shift { Action::Redo } else { Action::Undo }),
            'y' => Some(Action::Redo),
            '0' => Some(if m.alt {
                Action::ActualSize
            } else {
                Action::FitToWindow
            }),
            ' ' => Some(Action::ZoomIn), // Cmd/Ctrl+Space (spec §3; see shell note re: Spotlight)
            // mac alternative to Cmd+Space, which is reserved by Spotlight on
            // most machines (Codex review, §3 shell note): primary+`=`/`+` zooms
            // in, primary+`-` zooms out. The primary modifier means these never
            // collide with the bare `-` (RotateLeft) or Shift+`-` (US
            // RotateRight) shortcuts, which carry no primary modifier.
            '=' | '+' => Some(Action::ZoomIn),
            '-' => Some(Action::ZoomOut),
            // Any other primary combo (e.g. Cmd+O open, Cmd+S save) is left to
            // the OS/menu — do not steal it.
            _ => None,
        };
    }

    // Option/Alt (without primary).
    if m.alt {
        // Opt+Space zooms out. Opt+letter on mac produces special glyphs, so
        // any other Alt combo is resolved via the physical fallback instead.
        return if ch == ' ' {
            Some(Action::ZoomOut)
        } else {
            None
        };
    }

    // Bare keys (no primary, no alt; Shift may be set).
    match ch {
        'p' | 'P' => Some(Action::SelectTool(Tool::Pen)),
        'e' | 'E' => Some(Action::SelectTool(Tool::Eraser)),
        'u' | 'U' => Some(Action::SelectTool(Tool::Line)),
        'a' | 'A' => Some(Action::SelectTool(Tool::Arrow)),
        'r' | 'R' => Some(Action::SelectTool(Tool::Rect)),
        'o' | 'O' => Some(Action::SelectTool(Tool::Ellipse)),
        't' | 'T' => Some(Action::SelectTool(Tool::Text)),
        'i' | 'I' => Some(Action::Eyedropper),
        'x' | 'X' => Some(Action::SwapColor),
        'c' | 'C' => Some(Action::TransparentColor),
        '[' => Some(Action::BrushSmaller),
        ']' => Some(Action::BrushLarger),
        '-' => Some(Action::RotateLeft),
        '^' => Some(Action::RotateRight),
        // US-layout alternative for right-rotate: Shift+`-` yields `_`
        // (spec §3.1: "US 配列は Shift+`-` を代替併設").
        '_' => Some(Action::RotateRight),
        _ => None,
    }
}

fn resolve_physical(key: PhysicalKey, m: Modifiers) -> Option<Action> {
    use PhysicalKey as K;

    if m.primary {
        return match key {
            K::KeyZ => Some(if m.shift { Action::Redo } else { Action::Undo }),
            K::KeyY => Some(Action::Redo),
            K::Digit0 => Some(if m.alt {
                Action::ActualSize
            } else {
                Action::FitToWindow
            }),
            K::Space => Some(Action::ZoomIn),
            _ => None,
        };
    }

    if m.alt {
        return match key {
            K::Space => Some(Action::ZoomOut),
            _ => None,
        };
    }

    match key {
        K::KeyP => Some(Action::SelectTool(Tool::Pen)),
        K::KeyE => Some(Action::SelectTool(Tool::Eraser)),
        K::KeyU => Some(Action::SelectTool(Tool::Line)),
        K::KeyA => Some(Action::SelectTool(Tool::Arrow)),
        K::KeyR => Some(Action::SelectTool(Tool::Rect)),
        K::KeyO => Some(Action::SelectTool(Tool::Ellipse)),
        K::KeyT => Some(Action::SelectTool(Tool::Text)),
        K::KeyI => Some(Action::Eyedropper),
        K::KeyX => Some(Action::SwapColor),
        K::KeyC => Some(Action::TransparentColor),
        K::BracketLeft => Some(Action::BrushSmaller),
        K::BracketRight => Some(Action::BrushLarger),
        // Physical `-`: bare = left rotate; with Shift = right rotate (the
        // array-independent recovery of the US Shift+`-` alternative).
        K::Minus => Some(if m.shift {
            Action::RotateRight
        } else {
            Action::RotateLeft
        }),
        K::Caret => Some(Action::RotateRight),
        // Frame navigation is physical-only (no character): PageUp/PageDown,
        // chosen so they never collide with markup keys (spec §3 note / §2.2).
        K::PageUp => Some(Action::PrevFrame),
        K::PageDown => Some(Action::NextFrame),
        K::ArrowLeft | K::ArrowRight | K::ArrowUp | K::ArrowDown => resolve_arrows(key, m),
        _ => None,
    }
}

// ── Photoshop preset (V1.1 default) ──────────────────────────────────────
//
// Verbatim copies of Photoshop's default shortcuts where an Akapen action
// exists for them:
//   B = Brush(Pen) / E = Eraser / T = Type / I = Eyedropper / X = swap colors
//   [ ] = brush size / Ctrl+Z undo / Ctrl+Shift+Z redo / Ctrl+Alt+Z step back
//   Ctrl+= (+) zoom in / Ctrl+- zoom out / Ctrl+0 fit / Ctrl+1 = 100%
//   Ctrl+Space / Alt+Space momentary zoom / R = rotate-view tool
// Deliberate differences from the CSP table:
//   - Ctrl+Y is UNMAPPED (Photoshop: proof colors, never redo).
//   - R rotates the view (right; Shift+R left) instead of the Rect tool —
//     Photoshop's R is the rotate-view tool and Akapen's rotate is discrete.
//   - bare `-` / `^` are unmapped (no such Photoshop shortcut; rotation is on
//     R / Shift+R in this preset).
// Retained Akapen-only keys (no Photoshop collision, feature has no PS
// equivalent): P pen, U line, A arrow, O ellipse, C transparent color,
// PageUp/PageDown + arrow frame stepping.

fn resolve_char_ps(ch: Option<char>, m: Modifiers) -> Option<Action> {
    let ch = ch?;
    let lower = ch.to_ascii_lowercase();

    if m.primary {
        return match lower {
            // Ctrl+Z undo; Ctrl+Shift+Z redo; Ctrl+Alt+Z = Photoshop's "step
            // backward", folded into plain undo here (Akapen history is linear).
            'z' => Some(if m.shift { Action::Redo } else { Action::Undo }),
            '0' => Some(if m.alt {
                Action::ActualSize
            } else {
                Action::FitToWindow
            }),
            '1' => Some(Action::ActualSize),
            ' ' => Some(Action::ZoomIn),
            '=' | '+' => Some(Action::ZoomIn),
            '-' => Some(Action::ZoomOut),
            _ => None,
        };
    }

    if m.alt {
        return if ch == ' ' {
            Some(Action::ZoomOut)
        } else {
            None
        };
    }

    match ch {
        'b' | 'B' => Some(Action::SelectTool(Tool::Pen)),
        'p' | 'P' => Some(Action::SelectTool(Tool::Pen)),
        'e' | 'E' => Some(Action::SelectTool(Tool::Eraser)),
        'u' | 'U' => Some(Action::SelectTool(Tool::Line)),
        'a' | 'A' => Some(Action::SelectTool(Tool::Arrow)),
        'o' | 'O' => Some(Action::SelectTool(Tool::Ellipse)),
        't' | 'T' => Some(Action::SelectTool(Tool::Text)),
        'i' | 'I' => Some(Action::Eyedropper),
        'x' | 'X' => Some(Action::SwapColor),
        'c' | 'C' => Some(Action::TransparentColor),
        'r' => Some(Action::RotateRight),
        'R' => Some(if m.shift {
            Action::RotateLeft
        } else {
            Action::RotateRight
        }),
        '[' => Some(Action::BrushSmaller),
        ']' => Some(Action::BrushLarger),
        _ => None,
    }
}

fn resolve_physical_ps(key: PhysicalKey, m: Modifiers) -> Option<Action> {
    use PhysicalKey as K;

    if m.primary {
        return match key {
            K::KeyZ => Some(if m.shift { Action::Redo } else { Action::Undo }),
            K::Digit0 => Some(if m.alt {
                Action::ActualSize
            } else {
                Action::FitToWindow
            }),
            K::Digit1 => Some(Action::ActualSize),
            K::Space => Some(Action::ZoomIn),
            _ => None,
        };
    }

    if m.alt {
        return match key {
            K::Space => Some(Action::ZoomOut),
            _ => None,
        };
    }

    match key {
        K::KeyB | K::KeyP => Some(Action::SelectTool(Tool::Pen)),
        K::KeyE => Some(Action::SelectTool(Tool::Eraser)),
        K::KeyU => Some(Action::SelectTool(Tool::Line)),
        K::KeyA => Some(Action::SelectTool(Tool::Arrow)),
        K::KeyO => Some(Action::SelectTool(Tool::Ellipse)),
        K::KeyT => Some(Action::SelectTool(Tool::Text)),
        K::KeyI => Some(Action::Eyedropper),
        K::KeyX => Some(Action::SwapColor),
        K::KeyC => Some(Action::TransparentColor),
        K::KeyR => Some(if m.shift {
            Action::RotateLeft
        } else {
            Action::RotateRight
        }),
        K::BracketLeft => Some(Action::BrushSmaller),
        K::BracketRight => Some(Action::BrushLarger),
        K::PageUp => Some(Action::PrevFrame),
        K::PageDown => Some(Action::NextFrame),
        K::ArrowLeft | K::ArrowRight | K::ArrowUp | K::ArrowDown => resolve_arrows(key, m),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(ch: Option<char>, physical: PhysicalKey, m: Modifiers) -> KeyInput {
        KeyInput {
            ch,
            physical,
            mods: m,
            composing: false,
            text_editing: false,
        }
    }

    // ── Test vectors (spec §3 shortcut table is the source of truth) ──
    //
    // Each row: (produced char, physical key, modifiers) -> expected Action.
    // Written by hand from spec §3.1 because the VEDA reference `lib/annotate-*`
    // is not present in this repo; this table *is* the fixed contract.

    #[test]
    fn tool_letters_map_by_character() {
        let cases = [
            ('p', PhysicalKey::KeyP, Action::SelectTool(Tool::Pen)),
            ('e', PhysicalKey::KeyE, Action::SelectTool(Tool::Eraser)),
            ('u', PhysicalKey::KeyU, Action::SelectTool(Tool::Line)),
            ('a', PhysicalKey::KeyA, Action::SelectTool(Tool::Arrow)),
            ('r', PhysicalKey::KeyR, Action::SelectTool(Tool::Rect)),
            ('o', PhysicalKey::KeyO, Action::SelectTool(Tool::Ellipse)),
            ('t', PhysicalKey::KeyT, Action::SelectTool(Tool::Text)),
            ('i', PhysicalKey::KeyI, Action::Eyedropper),
            ('x', PhysicalKey::KeyX, Action::SwapColor),
            ('c', PhysicalKey::KeyC, Action::TransparentColor),
        ];
        for (ch, phys, want) in cases {
            assert_eq!(
                resolve(key(Some(ch), phys, Modifiers::NONE)),
                Some(want),
                "char {ch}"
            );
            // Uppercase (Shift held) resolves the same tool.
            let up = ch.to_ascii_uppercase();
            let mut m = Modifiers::NONE;
            m.shift = true;
            assert_eq!(
                resolve(key(Some(up), phys, m)),
                Some(want),
                "shifted char {up}"
            );
        }
    }

    #[test]
    fn undo_redo_variants() {
        let m = Modifiers::PRIMARY;
        assert_eq!(
            resolve(key(Some('z'), PhysicalKey::KeyZ, m)),
            Some(Action::Undo)
        );
        let mut ms = Modifiers::PRIMARY;
        ms.shift = true;
        assert_eq!(
            resolve(key(Some('z'), PhysicalKey::KeyZ, ms)),
            Some(Action::Redo),
            "Cmd+Shift+Z = redo"
        );
        assert_eq!(
            resolve(key(Some('y'), PhysicalKey::KeyY, m)),
            Some(Action::Redo),
            "Cmd+Y = redo"
        );
        // Bare z / y are not actions.
        assert_eq!(
            resolve(key(Some('z'), PhysicalKey::KeyZ, Modifiers::NONE)),
            None
        );
    }

    #[test]
    fn zoom_fit_and_actual_size() {
        let m = Modifiers::PRIMARY;
        assert_eq!(
            resolve(key(Some(' '), PhysicalKey::Space, m)),
            Some(Action::ZoomIn),
            "Cmd+Space"
        );
        let mut alt = Modifiers::NONE;
        alt.alt = true;
        assert_eq!(
            resolve(key(Some(' '), PhysicalKey::Space, alt)),
            Some(Action::ZoomOut),
            "Opt+Space"
        );
        assert_eq!(
            resolve(key(Some('0'), PhysicalKey::Digit0, m)),
            Some(Action::FitToWindow),
            "Cmd+0"
        );
        let mut cmd_alt = Modifiers::PRIMARY;
        cmd_alt.alt = true;
        assert_eq!(
            resolve(key(Some('0'), PhysicalKey::Digit0, cmd_alt)),
            Some(Action::ActualSize),
            "Cmd+Opt+0"
        );
    }

    #[test]
    fn brush_size_by_character() {
        assert_eq!(
            resolve(key(Some('['), PhysicalKey::BracketLeft, Modifiers::NONE)),
            Some(Action::BrushSmaller)
        );
        assert_eq!(
            resolve(key(Some(']'), PhysicalKey::BracketRight, Modifiers::NONE)),
            Some(Action::BrushLarger)
        );
    }

    #[test]
    fn rotation_jis_and_us_variants() {
        // JIS: bare `-` and bare `^`.
        assert_eq!(
            resolve(key(Some('-'), PhysicalKey::Minus, Modifiers::NONE)),
            Some(Action::RotateLeft)
        );
        assert_eq!(
            resolve(key(Some('^'), PhysicalKey::Caret, Modifiers::NONE)),
            Some(Action::RotateRight)
        );
        // US alternative for right-rotate: Shift+`-` produces `_`.
        let mut shift = Modifiers::NONE;
        shift.shift = true;
        assert_eq!(
            resolve(key(Some('_'), PhysicalKey::Minus, shift)),
            Some(Action::RotateRight),
            "US Shift+- = right rotate"
        );
    }

    #[test]
    fn mac_zoom_alternative_to_cmd_space() {
        // Codex review: Cmd+Space is reserved by Spotlight on most macs, so a
        // primary+`=`/`+`/`-` alternative is provided (spec §3 shell note).
        let m = Modifiers::PRIMARY;
        assert_eq!(
            resolve(key(Some('='), PhysicalKey::Other, m)),
            Some(Action::ZoomIn),
            "Cmd+= = zoom in"
        );
        let mut shift = Modifiers::PRIMARY;
        shift.shift = true;
        assert_eq!(
            resolve(key(Some('+'), PhysicalKey::Other, shift)),
            Some(Action::ZoomIn),
            "Cmd++ = zoom in"
        );
        assert_eq!(
            resolve(key(Some('-'), PhysicalKey::Minus, m)),
            Some(Action::ZoomOut),
            "Cmd+- = zoom out"
        );
        // The primary modifier keeps these from colliding with the un-modified
        // rotate shortcuts on the same characters.
        assert_eq!(
            resolve(key(Some('-'), PhysicalKey::Minus, Modifiers::NONE)),
            Some(Action::RotateLeft),
            "bare - still rotates left"
        );
        let mut shift_only = Modifiers::NONE;
        shift_only.shift = true;
        assert_eq!(
            resolve(key(Some('_'), PhysicalKey::Minus, shift_only)),
            Some(Action::RotateRight),
            "Shift+- (US) still rotates right"
        );
    }

    #[test]
    fn us_layout_keycode24_position_does_not_rotate() {
        // Regression (Codex review, High): on a US keyboard, the key at the
        // JIS-`^` physical position produces `=`, not `^`, and must NOT rotate.
        // The mac shell no longer maps that keyCode to PhysicalKey::Caret (only
        // an actual JIS `^` character reaches RotateRight), so here we simulate
        // the US case as the shell now does: character `=`, physical `Other`.
        assert_eq!(
            resolve(key(Some('='), PhysicalKey::Other, Modifiers::NONE)),
            None,
            "US '=' at the JIS caret position must not resolve to any action"
        );
        // JIS `^` (character) still rotates right, with or without a known
        // physical code — this must keep working.
        assert_eq!(
            resolve(key(Some('^'), PhysicalKey::Caret, Modifiers::NONE)),
            Some(Action::RotateRight),
            "JIS '^' still rotates right"
        );
        assert_eq!(
            resolve(key(Some('^'), PhysicalKey::Other, Modifiers::NONE)),
            Some(Action::RotateRight),
            "JIS '^' rotates right by character even if physical code is unmapped"
        );
    }

    #[test]
    fn array_independent_physical_fallback_when_char_missing() {
        // Simulate a layout/IME that produced no usable character: only the
        // physical key is known. Fixed-position keys still resolve.
        assert_eq!(
            resolve(key(None, PhysicalKey::BracketLeft, Modifiers::NONE)),
            Some(Action::BrushSmaller)
        );
        assert_eq!(
            resolve(key(None, PhysicalKey::Minus, Modifiers::NONE)),
            Some(Action::RotateLeft)
        );
        let mut shift = Modifiers::NONE;
        shift.shift = true;
        assert_eq!(
            resolve(key(None, PhysicalKey::Minus, shift)),
            Some(Action::RotateRight),
            "physical - + shift = right rotate"
        );
        assert_eq!(
            resolve(key(None, PhysicalKey::Caret, Modifiers::NONE)),
            Some(Action::RotateRight)
        );
        assert_eq!(
            resolve(key(None, PhysicalKey::KeyP, Modifiers::NONE)),
            Some(Action::SelectTool(Tool::Pen))
        );
    }

    #[test]
    fn frame_nav_is_physical_only() {
        assert_eq!(
            resolve(key(None, PhysicalKey::PageUp, Modifiers::NONE)),
            Some(Action::PrevFrame)
        );
        assert_eq!(
            resolve(key(None, PhysicalKey::PageDown, Modifiers::NONE)),
            Some(Action::NextFrame)
        );
    }

    #[test]
    fn ime_and_text_editing_guards_swallow_nothing() {
        // While composing or in a text field, even a known key returns None.
        let composing = KeyInput {
            ch: Some('p'),
            physical: PhysicalKey::KeyP,
            mods: Modifiers::NONE,
            composing: true,
            text_editing: false,
        };
        assert_eq!(resolve(composing), None);
        let typing = KeyInput {
            ch: Some('['),
            physical: PhysicalKey::BracketLeft,
            mods: Modifiers::NONE,
            composing: false,
            text_editing: true,
        };
        assert_eq!(resolve(typing), None);
    }

    #[test]
    fn open_and_save_are_not_stolen() {
        // Cmd+O (open) and Cmd+S (save) must fall through to the menu.
        let m = Modifiers::PRIMARY;
        assert_eq!(resolve(key(Some('o'), PhysicalKey::KeyO, m)), None);
        assert_eq!(resolve(key(Some('s'), PhysicalKey::Other, m)), None);
        // But bare `o` is the ellipse tool (spec §3.1), independent of Cmd+O.
        assert_eq!(
            resolve(key(Some('o'), PhysicalKey::KeyO, Modifiers::NONE)),
            Some(Action::SelectTool(Tool::Ellipse))
        );
    }

    // ── Arrow keys (shared by both presets, physical-only) ──

    #[test]
    fn arrows_step_frames_and_zoom_in_both_presets() {
        for preset in [KeymapPreset::ClipStudio, KeymapPreset::Photoshop] {
            let cases = [
                (PhysicalKey::ArrowLeft, Action::PrevFrame),
                (PhysicalKey::ArrowRight, Action::NextFrame),
                (PhysicalKey::ArrowUp, Action::ZoomIn),
                (PhysicalKey::ArrowDown, Action::ZoomOut),
            ];
            for (phys, want) in cases {
                assert_eq!(
                    resolve_preset(preset, key(None, phys, Modifiers::NONE)),
                    Some(want),
                    "{preset:?} {phys:?}"
                );
            }
            // Modified arrows are left to the shell / OS.
            assert_eq!(
                resolve_preset(
                    preset,
                    key(None, PhysicalKey::ArrowLeft, Modifiers::PRIMARY)
                ),
                None,
                "{preset:?} primary+Left must not step"
            );
        }
    }

    // ── Photoshop preset (V1.1 default) ──

    #[test]
    fn photoshop_brush_and_tools() {
        let ps = KeymapPreset::Photoshop;
        // B = brush is the Photoshop-verbatim binding; P is retained.
        assert_eq!(
            resolve_preset(ps, key(Some('b'), PhysicalKey::KeyB, Modifiers::NONE)),
            Some(Action::SelectTool(Tool::Pen))
        );
        assert_eq!(
            resolve_preset(ps, key(Some('p'), PhysicalKey::KeyP, Modifiers::NONE)),
            Some(Action::SelectTool(Tool::Pen))
        );
        assert_eq!(
            resolve_preset(ps, key(Some('e'), PhysicalKey::KeyE, Modifiers::NONE)),
            Some(Action::SelectTool(Tool::Eraser))
        );
        // B means nothing in the CSP preset (regression guard).
        assert_eq!(
            resolve_preset(
                KeymapPreset::ClipStudio,
                key(Some('b'), PhysicalKey::KeyB, Modifiers::NONE)
            ),
            None
        );
    }

    #[test]
    fn photoshop_rotate_on_r() {
        let ps = KeymapPreset::Photoshop;
        assert_eq!(
            resolve_preset(ps, key(Some('r'), PhysicalKey::KeyR, Modifiers::NONE)),
            Some(Action::RotateRight),
            "R = rotate view (Photoshop's rotate-view tool)"
        );
        let mut shift = Modifiers::NONE;
        shift.shift = true;
        assert_eq!(
            resolve_preset(ps, key(Some('R'), PhysicalKey::KeyR, shift)),
            Some(Action::RotateLeft),
            "Shift+R = rotate the other way"
        );
        // Physical fallback keeps the same meaning.
        assert_eq!(
            resolve_preset(ps, key(None, PhysicalKey::KeyR, Modifiers::NONE)),
            Some(Action::RotateRight)
        );
        // Bare `-` / `^` are NOT rotation in the Photoshop preset.
        assert_eq!(
            resolve_preset(ps, key(Some('-'), PhysicalKey::Minus, Modifiers::NONE)),
            None
        );
        assert_eq!(
            resolve_preset(ps, key(Some('^'), PhysicalKey::Caret, Modifiers::NONE)),
            None
        );
    }

    #[test]
    fn photoshop_history_and_view() {
        let ps = KeymapPreset::Photoshop;
        let m = Modifiers::PRIMARY;
        assert_eq!(
            resolve_preset(ps, key(Some('z'), PhysicalKey::KeyZ, m)),
            Some(Action::Undo)
        );
        let mut ms = Modifiers::PRIMARY;
        ms.shift = true;
        assert_eq!(
            resolve_preset(ps, key(Some('z'), PhysicalKey::KeyZ, ms)),
            Some(Action::Redo),
            "Ctrl+Shift+Z = redo (Photoshop default)"
        );
        let mut ma = Modifiers::PRIMARY;
        ma.alt = true;
        assert_eq!(
            resolve_preset(ps, key(Some('z'), PhysicalKey::KeyZ, ma)),
            Some(Action::Undo),
            "Ctrl+Alt+Z = step backward = undo"
        );
        // Ctrl+Y is proof colors in Photoshop — must NOT redo here.
        assert_eq!(
            resolve_preset(ps, key(Some('y'), PhysicalKey::KeyY, m)),
            None
        );
        assert_eq!(
            resolve_preset(ps, key(Some('0'), PhysicalKey::Digit0, m)),
            Some(Action::FitToWindow)
        );
        assert_eq!(
            resolve_preset(ps, key(Some('1'), PhysicalKey::Digit1, m)),
            Some(Action::ActualSize),
            "Ctrl+1 = 100% (Photoshop default)"
        );
        assert_eq!(
            resolve_preset(ps, key(Some('='), PhysicalKey::Other, m)),
            Some(Action::ZoomIn)
        );
        assert_eq!(
            resolve_preset(ps, key(Some('-'), PhysicalKey::Minus, m)),
            Some(Action::ZoomOut)
        );
    }

    #[test]
    fn photoshop_guards_still_apply() {
        let composing = KeyInput {
            ch: Some('b'),
            physical: PhysicalKey::KeyB,
            mods: Modifiers::NONE,
            composing: true,
            text_editing: false,
        };
        assert_eq!(resolve_preset(KeymapPreset::Photoshop, composing), None);
    }

    #[test]
    fn clipstudio_preset_matches_legacy_resolve() {
        // `resolve` must stay the CSP table so existing callers (mac shell,
        // V1.0 Windows shells) keep their behavior.
        let input = key(Some('r'), PhysicalKey::KeyR, Modifiers::NONE);
        assert_eq!(resolve(input), Some(Action::SelectTool(Tool::Rect)));
        assert_eq!(
            resolve_preset(KeymapPreset::ClipStudio, input),
            Some(Action::SelectTool(Tool::Rect))
        );
    }

    #[test]
    fn plain_space_is_not_a_discrete_action() {
        // Plain Space is a momentary-pan hold handled by the shell, not a
        // discrete action here.
        assert_eq!(
            resolve(key(Some(' '), PhysicalKey::Space, Modifiers::NONE)),
            None
        );
    }
}
