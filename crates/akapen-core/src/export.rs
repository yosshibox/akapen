//! The 3-file export (spec §4.3 / §7.2): transparent strokes PNG, flat PNG,
//! and the `veda-annot-1` vector JSON. This is the VEDA re-import compatibility
//! key; **every stroke keeps its per-point pressure** (spec §5.5).

use crate::raster::{unpack_rgba, RgbaBuffer};
use crate::stroke::{Stroke, Tool};
use crate::vector::{StrokeDoc, VectorDoc, VectorPoint};

/// The three artifacts produced for one reviewed image.
pub struct ExportSet {
    /// ① Transparent PNG, stroke pixels only, at natural size.
    pub strokes_png: Vec<u8>,
    /// ② Flat PNG: strokes composited over the original background.
    pub flat_png: Vec<u8>,
    /// ③ Vector document (serialize with `.to_json()`).
    pub vector: VectorDoc,
}

/// Encodes a straight-RGBA8 buffer to PNG bytes.
pub fn encode_png(buf: &RgbaBuffer) -> Vec<u8> {
    let mut out = Vec::new();
    {
        let mut encoder = png::Encoder::new(&mut out, buf.width, buf.height);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        let mut writer = encoder
            .write_header()
            .expect("png header write should not fail for an in-memory sink");
        writer
            .write_image_data(&buf.data)
            .expect("png image write should not fail for an in-memory sink");
    }
    out
}

/// Maps a [`Tool`] to the vector-doc `kind` tag.
pub fn tool_kind(tool: Tool) -> &'static str {
    match tool {
        Tool::Pen => "pen",
        Tool::Eraser => "eraser",
        Tool::Line => "line",
        Tool::Arrow => "arrow",
        Tool::Rect => "rect",
        Tool::Ellipse => "ellipse",
        Tool::Text => "text",
    }
}

/// Formats a packed `0xRRGGBBAA` color as a CSS `#rrggbb` string (the alpha is
/// carried by `opacity`, matching the reference payload).
pub fn color_to_css(color: u32) -> String {
    let (r, g, b, _a) = unpack_rgba(color);
    format!("#{r:02x}{g:02x}{b:02x}")
}

/// Builds a [`StrokeDoc`] from an in-memory [`Stroke`], preserving every
/// point's raw pressure.
pub fn stroke_to_doc(stroke: &Stroke, smoothing_tag: &str) -> StrokeDoc {
    StrokeDoc {
        kind: tool_kind(stroke.tool).to_string(),
        points: stroke
            .points
            .iter()
            .map(|p| VectorPoint {
                x: p.x,
                y: p.y,
                p: p.p,
            })
            .collect(),
        size: stroke.size,
        color: color_to_css(stroke.color),
        erase: stroke.tool == Tool::Eraser || stroke.erase,
        opacity: stroke.opacity,
        smoothing: smoothing_tag.to_string(),
        timecode: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::brush::Brush;
    use crate::raster::{bake_stroke, flatten};
    use crate::smoothing::Smoothing;
    use crate::stroke::Point;

    #[test]
    fn png_roundtrips_pixels() {
        let mut buf = RgbaBuffer::transparent(3, 2);
        buf.data[0..4].copy_from_slice(&[255, 0, 0, 255]);
        let png_bytes = encode_png(&buf);
        // Decode back with the same crate and compare.
        let decoder = png::Decoder::new(&png_bytes[..]);
        let mut reader = decoder.read_info().unwrap();
        let mut out = vec![0u8; reader.output_buffer_size()];
        let info = reader.next_frame(&mut out).unwrap();
        assert_eq!((info.width, info.height), (3, 2));
        assert_eq!(&out[0..4], &[255, 0, 0, 255]);
    }

    #[test]
    fn strokes_and_flat_pngs_differ_but_both_encode() {
        let bg = RgbaBuffer::from_rgba(16, 16, vec![255; 16 * 16 * 4]);
        let mut layer = RgbaBuffer::transparent(16, 16);
        let mut s = Stroke::new(Tool::Pen, 20.0, 0xFF0000FF);
        s.points = vec![Point::new(8.0, 8.0, 1.0)];
        bake_stroke(&mut layer, &s, &Brush::from_size(20.0), Smoothing::Off);
        let flat = flatten(&bg, &layer);
        let strokes_png = encode_png(&layer);
        let flat_png = encode_png(&flat);
        assert!(!strokes_png.is_empty() && !flat_png.is_empty());
        assert_ne!(strokes_png, flat_png);
    }

    #[test]
    fn stroke_doc_preserves_pressure_and_kind() {
        let mut s = Stroke::new(Tool::Pen, 6.0, 0xFF0000FF);
        s.points = vec![Point::new(1.0, 2.0, 0.3), Point::new(3.0, 4.0, 0.9)];
        let doc = stroke_to_doc(&s, "weak");
        assert_eq!(doc.kind, "pen");
        assert_eq!(doc.color, "#ff0000");
        assert_eq!(doc.points.len(), 2);
        assert_eq!(doc.points[0].p, 0.3);
        assert_eq!(doc.points[1].p, 0.9);
        assert_eq!(doc.smoothing, "weak");
    }

    #[test]
    fn eraser_kind_and_flag() {
        let mut s = Stroke::new(Tool::Eraser, 10.0, 0x00000000);
        s.points = vec![Point::new(0.0, 0.0, 1.0)];
        let doc = stroke_to_doc(&s, "off");
        assert_eq!(doc.kind, "eraser");
        assert!(doc.erase);
    }
}
