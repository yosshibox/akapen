//! Frame-sequence navigation (spec §4.5).
//!
//! Two strategies combine: the sequence-token ±1 (from [`crate::next_sequence_name`]
//! / [`crate::prev_sequence_name`], preserving zero padding) and a natural-sort
//! of the folder's supported images as a fallback when the token target does
//! not exist or there is no digit run. This mirrors the reference behaviour:
//! "next/prev frame, with the directory order as insurance for gaps".

use crate::decode::is_supported_image;
use crate::{next_sequence_name, prev_sequence_name};
use std::path::{Path, PathBuf};

/// Navigation direction.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Direction {
    Next,
    Prev,
}

/// Lists supported image files in `dir`, natural-sorted (so `c2 < c10`).
pub fn list_images(dir: impl AsRef<Path>) -> std::io::Result<Vec<PathBuf>> {
    let mut files: Vec<PathBuf> = std::fs::read_dir(dir)?
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.is_file() && is_supported_image(p))
        .collect();
    files.sort_by(|a, b| natural_cmp(&file_name(a), &file_name(b)));
    Ok(files)
}

fn file_name(p: &Path) -> String {
    p.file_name()
        .and_then(|s| s.to_str())
        .unwrap_or("")
        .to_string()
}

/// Resolves the neighbor of `current` within `files` (already natural-sorted).
///
/// Prefers the sequence-token target (`±1`, padding-preserving) when a file
/// with that stem exists in the list; otherwise falls back to the adjacent
/// entry in natural order. Returns `None` at the ends.
pub fn neighbor(current: &Path, files: &[PathBuf], dir: Direction) -> Option<PathBuf> {
    let stem = current.file_stem().and_then(|s| s.to_str())?;
    let ext = current.extension().and_then(|s| s.to_str());

    // 1) sequence-token target with the same extension, if present in the list.
    let token = match dir {
        Direction::Next => next_sequence_name(stem),
        Direction::Prev => prev_sequence_name(stem),
    };
    if let Some(tok_stem) = token {
        if let Some(hit) = files.iter().find(|p| {
            p.file_stem().and_then(|s| s.to_str()) == Some(tok_stem.as_str())
                && p.extension().and_then(|s| s.to_str()) == ext
        }) {
            return Some(hit.clone());
        }
    }

    // 2) fallback: adjacent entry in the natural-sorted listing.
    let idx = files.iter().position(|p| p == current)?;
    match dir {
        Direction::Next => files.get(idx + 1).cloned(),
        Direction::Prev => {
            if idx == 0 {
                None
            } else {
                files.get(idx - 1).cloned()
            }
        }
    }
}

/// Natural (human) comparison: runs of digits compare by numeric value so
/// `c2 < c10`. Non-digit runs compare byte-wise, case-insensitively.
fn natural_cmp(a: &str, b: &str) -> std::cmp::Ordering {
    use std::cmp::Ordering;
    let (a, b) = (a.as_bytes(), b.as_bytes());
    let (mut i, mut j) = (0, 0);
    while i < a.len() && j < b.len() {
        let (ca, cb) = (a[i], b[j]);
        if ca.is_ascii_digit() && cb.is_ascii_digit() {
            let si = i;
            let sj = j;
            while i < a.len() && a[i].is_ascii_digit() {
                i += 1;
            }
            while j < b.len() && b[j].is_ascii_digit() {
                j += 1;
            }
            // Compare numerically, ignoring leading zeros.
            let na = &a[si..i];
            let nb = &b[sj..j];
            let ta = na.iter().skip_while(|&&c| c == b'0').count();
            let tb = nb.iter().skip_while(|&&c| c == b'0').count();
            match ta.cmp(&tb) {
                Ordering::Equal => {}
                ord => return ord,
            }
            let da = &na[na.len() - ta..];
            let db = &nb[nb.len() - tb..];
            match da.cmp(db) {
                Ordering::Equal => {}
                ord => return ord,
            }
        } else {
            let (la, lb) = (a[i].to_ascii_lowercase(), b[j].to_ascii_lowercase());
            match la.cmp(&lb) {
                Ordering::Equal => {}
                ord => return ord,
            }
            i += 1;
            j += 1;
        }
    }
    a.len().cmp(&b.len())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn paths(names: &[&str]) -> Vec<PathBuf> {
        names
            .iter()
            .map(|n| PathBuf::from(format!("/w/{n}")))
            .collect()
    }

    #[test]
    fn natural_sort_orders_numbers_by_value() {
        let mut v = vec!["c10.png", "c2.png", "c1.png"];
        v.sort_by(|a, b| natural_cmp(a, b));
        assert_eq!(v, vec!["c1.png", "c2.png", "c10.png"]);
    }

    #[test]
    fn next_prefers_padded_token() {
        let files = paths(&["c001.png", "c002.png", "c003.png"]);
        let n = neighbor(Path::new("/w/c001.png"), &files, Direction::Next);
        assert_eq!(n, Some(PathBuf::from("/w/c002.png")));
        let p = neighbor(Path::new("/w/c003.png"), &files, Direction::Prev);
        assert_eq!(p, Some(PathBuf::from("/w/c002.png")));
    }

    #[test]
    fn falls_back_to_listing_order_across_gaps() {
        // c002 is missing → token target absent → use natural-order neighbor.
        let files = paths(&["c001.png", "c005.png", "c009.png"]);
        let n = neighbor(Path::new("/w/c001.png"), &files, Direction::Next);
        assert_eq!(n, Some(PathBuf::from("/w/c005.png")));
    }

    #[test]
    fn stops_at_the_ends() {
        let files = paths(&["c001.png", "c002.png"]);
        assert_eq!(
            neighbor(Path::new("/w/c002.png"), &files, Direction::Next),
            None
        );
        assert_eq!(
            neighbor(Path::new("/w/c001.png"), &files, Direction::Prev),
            None
        );
    }

    #[test]
    fn preserves_prefix_cut_number() {
        let files = paths(&["cut10_0004.png", "cut10_0005.png"]);
        let n = neighbor(Path::new("/w/cut10_0004.png"), &files, Direction::Next);
        assert_eq!(n, Some(PathBuf::from("/w/cut10_0005.png")));
    }

    #[test]
    fn token_only_matches_same_extension() {
        // c002.jpg present but current is png → token (png) misses, fall back
        // to natural order which yields the jpg as the adjacent entry.
        let files = paths(&["c001.png", "c002.jpg"]);
        let n = neighbor(Path::new("/w/c001.png"), &files, Direction::Next);
        assert_eq!(n, Some(PathBuf::from("/w/c002.jpg")));
    }

    #[test]
    fn lists_and_sorts_real_directory() {
        let dir = std::env::temp_dir().join(format!("akapen-seq-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        for n in ["c10.png", "c2.png", "notes.txt", "c1.png"] {
            std::fs::write(dir.join(n), b"x").unwrap();
        }
        let files = list_images(&dir).unwrap();
        let names: Vec<String> = files.iter().map(|p| file_name(p)).collect();
        assert_eq!(names, vec!["c1.png", "c2.png", "c10.png"]);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
