//! Image decode adapters (spec §4.1 / §7.1).
//!
//! The core never touches image formats — it only receives a decoded RGBA8
//! bitmap. This module is that boundary for the M1 CPU path: it turns a file on
//! disk into `(rgba, width, height)`. (On a real shell, mac ImageIO / Windows
//! WIC may decode instead and hand the same RGBA to the core; this pure-Rust
//! path is the portable fallback and what tests/CLI use.)

use std::path::Path;

/// A decoded image in straight RGBA8, natural (original) size.
#[derive(Debug, Clone, PartialEq)]
pub struct DecodedImage {
    pub width: u32,
    pub height: u32,
    /// `width * height * 4` bytes, row-major R,G,B,A.
    pub rgba: Vec<u8>,
}

/// Decodes an image file to RGBA8. Reads the original **read-only** (spec §4.1:
/// input is never written). Supported here: png/jpg/webp/bmp.
pub fn decode_rgba(path: impl AsRef<Path>) -> Result<DecodedImage, String> {
    let img = image::open(path.as_ref()).map_err(|e| format!("decode failed: {e}"))?;
    let rgba = img.to_rgba8();
    let (width, height) = rgba.dimensions();
    Ok(DecodedImage {
        width,
        height,
        rgba: rgba.into_raw(),
    })
}

/// Whether the extension is a background image format Akapen can open at M1.
pub fn is_supported_image(path: impl AsRef<Path>) -> bool {
    matches!(
        ext_lower(path.as_ref()).as_deref(),
        Some("png" | "jpg" | "jpeg" | "webp" | "bmp")
    )
}

pub(crate) fn ext_lower(path: &Path) -> Option<String> {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| e.to_ascii_lowercase())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn supported_extensions_are_recognized_case_insensitively() {
        assert!(is_supported_image("a/b/frame001.PNG"));
        assert!(is_supported_image("c001.jpg"));
        assert!(is_supported_image("c001.webp"));
        assert!(!is_supported_image("notes.txt"));
        assert!(!is_supported_image("movie.mp4"));
    }

    #[test]
    fn decodes_a_png_roundtrip() {
        // Write a 2x1 red/green PNG via the image crate, then decode it back.
        let dir = std::env::temp_dir().join(format!("akapen-dec-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("t001.png");
        image::save_buffer(
            &path,
            &[255, 0, 0, 255, 0, 255, 0, 255],
            2,
            1,
            image::ColorType::Rgba8,
        )
        .unwrap();
        let img = decode_rgba(&path).unwrap();
        assert_eq!((img.width, img.height), (2, 1));
        assert_eq!(&img.rgba[0..4], &[255, 0, 0, 255]);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
