//! Output target and 3-file naming (spec §4.3 / §4.7).
//!
//! Default output directory is `<input folder>/_review/` (owner decision
//! §10.1-6). The three artifacts are named
//! `<stem>.review.png` (flat), `<stem>.strokes.png` (transparent) and
//! `<stem>.strokes.json` (vector). On collision the resolver appends
//! `-2`, `-3`, … and **never overwrites** an existing file (VEDA's
//! COPYFILE_EXCL discipline).

use std::path::{Path, PathBuf};

/// Configurable suffixes for the artifacts (spec §4.7).
#[derive(Debug, Clone, PartialEq)]
pub struct OutputNaming {
    /// Suffix for the flat composite PNG. Default `review`.
    pub flat_suffix: String,
    /// Suffix for the transparent-strokes PNG and vector JSON. Default `strokes`.
    pub strokes_suffix: String,
}

impl Default for OutputNaming {
    fn default() -> Self {
        OutputNaming {
            flat_suffix: "review".to_string(),
            strokes_suffix: "strokes".to_string(),
        }
    }
}

/// Resolved absolute paths for one image's 3-file export.
#[derive(Debug, Clone, PartialEq)]
pub struct ReviewTarget {
    pub dir: PathBuf,
    pub flat_png: PathBuf,
    pub strokes_png: PathBuf,
    pub vector_json: PathBuf,
}

/// The default review directory for an input image: `<input folder>/_review/`.
pub fn default_review_dir(input: impl AsRef<Path>) -> PathBuf {
    input
        .as_ref()
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join("_review")
}

/// Resolves collision-free output paths for `input` inside `dir`.
///
/// `exists` tells the resolver whether a candidate path is taken (injected so
/// the logic is testable without the filesystem). The base names use the input
/// stem; if any of the three base files exist, the whole set is bumped to
/// `-2`, `-3`, … so the trio always shares one index and nothing is overwritten.
pub fn resolve_target(
    input: impl AsRef<Path>,
    dir: impl AsRef<Path>,
    naming: &OutputNaming,
    mut exists: impl FnMut(&Path) -> bool,
) -> ReviewTarget {
    let dir = dir.as_ref();
    let stem = input
        .as_ref()
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("image")
        .to_string();

    let build = |suffix_index: Option<u32>| {
        let tag = match suffix_index {
            None => String::new(),
            Some(n) => format!("-{n}"),
        };
        let flat = dir.join(format!("{stem}{tag}.{}.png", naming.flat_suffix));
        let strokes = dir.join(format!("{stem}{tag}.{}.png", naming.strokes_suffix));
        let vector = dir.join(format!("{stem}{tag}.{}.json", naming.strokes_suffix));
        (flat, strokes, vector)
    };

    // Try the bare name, then -2, -3, … until the whole trio is free.
    let mut candidate: Option<u32> = None;
    loop {
        let (flat, strokes, vector) = build(candidate);
        if !exists(&flat) && !exists(&strokes) && !exists(&vector) {
            return ReviewTarget {
                dir: dir.to_path_buf(),
                flat_png: flat,
                strokes_png: strokes,
                vector_json: vector,
            };
        }
        candidate = Some(candidate.map_or(2, |n| n + 1));
    }
}

/// Whether `input` already has a review artifact in `dir` (spec §4.5: mark
/// processed vs unprocessed frames in the filmstrip). Checks the bare flat PNG.
pub fn is_reviewed(
    input: impl AsRef<Path>,
    dir: impl AsRef<Path>,
    naming: &OutputNaming,
    mut exists: impl FnMut(&Path) -> bool,
) -> bool {
    let stem = input
        .as_ref()
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("image");
    let flat = dir
        .as_ref()
        .join(format!("{stem}.{}.png", naming.flat_suffix));
    exists(&flat)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;

    #[test]
    fn default_dir_is_review_next_to_input() {
        let d = default_review_dir("/work/cut10/c001.png");
        assert_eq!(d, PathBuf::from("/work/cut10/_review"));
    }

    #[test]
    fn bare_names_when_nothing_exists() {
        let naming = OutputNaming::default();
        let t = resolve_target("/w/c001.png", "/w/_review", &naming, |_| false);
        assert_eq!(t.flat_png, PathBuf::from("/w/_review/c001.review.png"));
        assert_eq!(t.strokes_png, PathBuf::from("/w/_review/c001.strokes.png"));
        assert_eq!(t.vector_json, PathBuf::from("/w/_review/c001.strokes.json"));
    }

    #[test]
    fn collision_bumps_the_whole_trio_and_never_overwrites() {
        let naming = OutputNaming::default();
        let mut taken: HashSet<PathBuf> = HashSet::new();
        taken.insert("/w/_review/c001.review.png".into());
        let t = resolve_target("/w/c001.png", "/w/_review", &naming, |p| taken.contains(p));
        // One member existed → the whole set shifts to -2.
        assert_eq!(t.flat_png, PathBuf::from("/w/_review/c001-2.review.png"));
        assert_eq!(
            t.strokes_png,
            PathBuf::from("/w/_review/c001-2.strokes.png")
        );
        assert_eq!(
            t.vector_json,
            PathBuf::from("/w/_review/c001-2.strokes.json")
        );
    }

    #[test]
    fn collision_walks_until_free() {
        let naming = OutputNaming::default();
        let taken: HashSet<PathBuf> = [
            "/w/_review/c001.review.png",
            "/w/_review/c001-2.strokes.json",
        ]
        .iter()
        .map(PathBuf::from)
        .collect();
        let t = resolve_target("/w/c001.png", "/w/_review", &naming, |p| taken.contains(p));
        assert_eq!(t.flat_png, PathBuf::from("/w/_review/c001-3.review.png"));
    }

    #[test]
    fn custom_suffixes_apply() {
        let naming = OutputNaming {
            flat_suffix: "akapen".into(),
            strokes_suffix: "ink".into(),
        };
        let t = resolve_target("/w/f.png", "/out", &naming, |_| false);
        assert_eq!(t.flat_png, PathBuf::from("/out/f.akapen.png"));
        assert_eq!(t.vector_json, PathBuf::from("/out/f.ink.json"));
    }

    #[test]
    fn is_reviewed_checks_the_flat_png() {
        let naming = OutputNaming::default();
        let taken: HashSet<PathBuf> = ["/w/_review/c001.review.png"]
            .iter()
            .map(PathBuf::from)
            .collect();
        assert!(is_reviewed("/w/c001.png", "/w/_review", &naming, |p| taken.contains(p)));
        assert!(!is_reviewed("/w/c002.png", "/w/_review", &naming, |p| {
            taken.contains(p)
        }));
    }
}
