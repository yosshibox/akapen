//! Akapen I/O scaffold (M0).
//!
//! The core (`akapen-core`) never touches image formats or the filesystem —
//! it only receives decoded RGBA bitmaps (spec §7.1). This crate is the home
//! for the I/O-side concerns that grow in later milestones: decode adapters,
//! frame-sequence detection (spec §4.5), output naming and the `_review/`
//! target (spec §4.3), and cross-platform path normalization (spec §7.3,
//! ported from `lib/annotate-target.js`).
//!
//! M1 adds: RGBA decode adapters ([`decode`]), the `_review/` output target
//! and collision-free naming ([`output`]), and forward/back sequence
//! resolution over a directory listing ([`sequence`]).

pub mod decode;
pub mod output;
pub mod sequence;

pub use decode::{decode_rgba, DecodedImage};
pub use output::{OutputNaming, ReviewTarget};
pub use sequence::{list_images, neighbor, Direction};

/// Increments the trailing digit run of a filename stem, preserving zero
/// padding and any prefix (spec §4.5). Returns `None` when there is no digit
/// run to increment.
///
/// Examples: `c001 -> c002`, `0001 -> 0002`, `A_012 -> A_013`,
/// `cut10_0004 -> cut10_0005` (only the **last** digit run moves).
pub fn next_sequence_name(stem: &str) -> Option<String> {
    // Find the last run of ASCII digits.
    let bytes = stem.as_bytes();
    let mut end = bytes.len();
    while end > 0 && !bytes[end - 1].is_ascii_digit() {
        end -= 1;
    }
    if end == 0 {
        return None;
    }
    let mut start = end;
    while start > 0 && bytes[start - 1].is_ascii_digit() {
        start -= 1;
    }
    let prefix = &stem[..start];
    let digits = &stem[start..end];
    let suffix = &stem[end..];
    let width = digits.len();
    // Parse as u128 to tolerate long runs; +1.
    let value: u128 = digits.parse().ok()?;
    let next = value + 1;
    Some(format!("{prefix}{next:0width$}{suffix}"))
}

/// Decrements the trailing digit run of a filename stem, preserving zero
/// padding and any prefix (spec §4.5). Returns `None` when there is no digit
/// run, or when the value is already `0` (no previous frame).
pub fn prev_sequence_name(stem: &str) -> Option<String> {
    let bytes = stem.as_bytes();
    let mut end = bytes.len();
    while end > 0 && !bytes[end - 1].is_ascii_digit() {
        end -= 1;
    }
    if end == 0 {
        return None;
    }
    let mut start = end;
    while start > 0 && bytes[start - 1].is_ascii_digit() {
        start -= 1;
    }
    let prefix = &stem[..start];
    let digits = &stem[start..end];
    let suffix = &stem[end..];
    let width = digits.len();
    let value: u128 = digits.parse().ok()?;
    if value == 0 {
        return None;
    }
    let prev = value - 1;
    Some(format!("{prefix}{prev:0width$}{suffix}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn increments_padded_prefix_tokens() {
        assert_eq!(next_sequence_name("c001").as_deref(), Some("c002"));
        assert_eq!(next_sequence_name("0001").as_deref(), Some("0002"));
        assert_eq!(next_sequence_name("A_012").as_deref(), Some("A_013"));
        assert_eq!(
            next_sequence_name("cut10_0004").as_deref(),
            Some("cut10_0005")
        );
    }

    #[test]
    fn rolls_over_padding_width() {
        assert_eq!(next_sequence_name("c099").as_deref(), Some("c100"));
        assert_eq!(next_sequence_name("009").as_deref(), Some("010"));
    }

    #[test]
    fn no_digits_returns_none() {
        assert_eq!(next_sequence_name("cover"), None);
    }

    #[test]
    fn prev_decrements_and_preserves_padding() {
        assert_eq!(prev_sequence_name("c002").as_deref(), Some("c001"));
        assert_eq!(prev_sequence_name("0010").as_deref(), Some("0009"));
        assert_eq!(
            prev_sequence_name("cut10_0005").as_deref(),
            Some("cut10_0004")
        );
    }

    #[test]
    fn prev_at_zero_or_no_digits_is_none() {
        assert_eq!(prev_sequence_name("c000"), None);
        assert_eq!(prev_sequence_name("cover"), None);
    }
}
