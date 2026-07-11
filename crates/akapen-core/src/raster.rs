//! CPU RGBA raster and the stroke-baking pipeline (spec §6.2 "incremental
//! bake").
//!
//! M1 uses a correctness-first CPU rasteriser; the wgpu path (spec §7.4) is a
//! later phase and shares this module's semantics. The key design carried over
//! from the reference / B24 lesson is **incremental baking**: committed strokes
//! are composited once into a transparent "strokes layer" buffer and never
//! re-drawn per frame. The flat (over-background) image is produced by
//! alpha-compositing that layer over the background at export time, which keeps
//! the eraser correct — erasing only lowers the strokes-layer alpha, letting
//! the untouched background show through.
//!
//! A stroke is rasterised into a per-stroke coverage mask (max coverage per
//! pixel, so overlapping stamps within one stroke do not accumulate alpha —
//! matching a single filled canvas path) and then composited into the layer:
//! `Pen` = source-over, `Eraser` = destination-out.

use crate::brush::Brush;
use crate::smoothing::{smooth_points, Smoothing};
use crate::stroke::{Stroke, Tool};

/// A straight (non-premultiplied) RGBA8 buffer.
#[derive(Debug, Clone, PartialEq)]
pub struct RgbaBuffer {
    pub width: u32,
    pub height: u32,
    /// `width * height * 4` bytes, row-major, R,G,B,A.
    pub data: Vec<u8>,
}

impl RgbaBuffer {
    /// Fully transparent buffer.
    pub fn transparent(width: u32, height: u32) -> Self {
        RgbaBuffer {
            width,
            height,
            data: vec![0u8; (width as usize) * (height as usize) * 4],
        }
    }

    /// Buffer from existing RGBA bytes. Panics if the length is wrong.
    pub fn from_rgba(width: u32, height: u32, data: Vec<u8>) -> Self {
        assert_eq!(
            data.len(),
            (width as usize) * (height as usize) * 4,
            "rgba length must be width*height*4"
        );
        RgbaBuffer {
            width,
            height,
            data,
        }
    }

    #[inline]
    fn idx(&self, x: u32, y: u32) -> usize {
        ((y as usize) * (self.width as usize) + (x as usize)) * 4
    }

    /// Alpha-composites `src` over `self` (source-over). Both must be the same
    /// size. Used to make the flat image (strokes layer over background).
    pub fn composite_over(&mut self, src: &RgbaBuffer) {
        assert_eq!(self.width, src.width);
        assert_eq!(self.height, src.height);
        for i in (0..self.data.len()).step_by(4) {
            let sa = src.data[i + 3] as f64 / 255.0;
            if sa <= 0.0 {
                continue;
            }
            let da = self.data[i + 3] as f64 / 255.0;
            let out_a = sa + da * (1.0 - sa);
            for c in 0..3 {
                let s = src.data[i + c] as f64 / 255.0;
                let d = self.data[i + c] as f64 / 255.0;
                let out = if out_a > 0.0 {
                    (s * sa + d * da * (1.0 - sa)) / out_a
                } else {
                    0.0
                };
                self.data[i + c] = (out * 255.0).round().clamp(0.0, 255.0) as u8;
            }
            self.data[i + 3] = (out_a * 255.0).round().clamp(0.0, 255.0) as u8;
        }
    }
}

/// Unpacks a packed `0xRRGGBBAA` color into straight `(r,g,b,a)` bytes.
pub fn unpack_rgba(color: u32) -> (u8, u8, u8, u8) {
    (
        ((color >> 24) & 0xff) as u8,
        ((color >> 16) & 0xff) as u8,
        ((color >> 8) & 0xff) as u8,
        (color & 0xff) as u8,
    )
}

/// A coverage mask confined to a bounding box, so a 4K stroke does not allocate
/// a 4K buffer (spec §6.2 dirty-rect discipline).
struct CoverageMask {
    x0: i64,
    y0: i64,
    w: usize,
    h: usize,
    /// `w * h` coverage values in `0..=1` (max coverage per pixel).
    cov: Vec<f32>,
}

impl CoverageMask {
    #[inline]
    fn accumulate(&mut self, px: i64, py: i64, c: f32) {
        if px < self.x0 || py < self.y0 {
            return;
        }
        let lx = (px - self.x0) as usize;
        let ly = (py - self.y0) as usize;
        if lx >= self.w || ly >= self.h {
            return;
        }
        let i = ly * self.w + lx;
        if c > self.cov[i] {
            self.cov[i] = c;
        }
    }
}

/// Stamps an anti-aliased filled disc into the coverage mask (max coverage).
fn stamp_disc(mask: &mut CoverageMask, cx: f64, cy: f64, radius: f64) {
    let r = radius.max(0.25);
    let min_x = (cx - r - 1.0).floor() as i64;
    let max_x = (cx + r + 1.0).ceil() as i64;
    let min_y = (cy - r - 1.0).floor() as i64;
    let max_y = (cy + r + 1.0).ceil() as i64;
    for py in min_y..=max_y {
        for px in min_x..=max_x {
            let dx = (px as f64 + 0.5) - cx;
            let dy = (py as f64 + 0.5) - cy;
            let dist = (dx * dx + dy * dy).sqrt();
            // 1px anti-aliased edge: full inside, ramp across the boundary.
            let cov = (r + 0.5 - dist).clamp(0.0, 1.0);
            if cov > 0.0 {
                mask.accumulate(px, py, cov as f32);
            }
        }
    }
}

/// Rasterises one free-hand stroke into a coverage mask, applying smoothing and
/// the per-point pressure→width mapping. Returns `None` for an empty stroke.
fn rasterize_free(
    stroke: &Stroke,
    brush: &Brush,
    smoothing: Smoothing,
    buf_w: u32,
    buf_h: u32,
) -> Option<CoverageMask> {
    if stroke.points.is_empty() {
        return None;
    }
    let pts = smooth_points(&stroke.points, smoothing);

    // Bounding box (with radius margin), clamped to the buffer.
    let max_r = pts
        .iter()
        .map(|p| brush.width_for(p.p) / 2.0)
        .fold(0.5_f64, f64::max);
    let margin = max_r + 2.0;
    let (mut lo_x, mut lo_y, mut hi_x, mut hi_y) = (f64::MAX, f64::MAX, f64::MIN, f64::MIN);
    for p in &pts {
        lo_x = lo_x.min(p.x);
        lo_y = lo_y.min(p.y);
        hi_x = hi_x.max(p.x);
        hi_y = hi_y.max(p.y);
    }
    let x0 = ((lo_x - margin).floor() as i64).max(0);
    let y0 = ((lo_y - margin).floor() as i64).max(0);
    let x1 = ((hi_x + margin).ceil() as i64).min(buf_w as i64);
    let y1 = ((hi_y + margin).ceil() as i64).min(buf_h as i64);
    if x1 <= x0 || y1 <= y0 {
        return None;
    }
    let w = (x1 - x0) as usize;
    let h = (y1 - y0) as usize;
    let mut mask = CoverageMask {
        x0,
        y0,
        w,
        h,
        cov: vec![0.0; w * h],
    };

    if pts.len() == 1 {
        let p = &pts[0];
        stamp_disc(&mut mask, p.x, p.y, brush.width_for(p.p) / 2.0);
        return Some(mask);
    }

    // Stamp discs densely along each segment (round caps + joins for free).
    for i in 1..pts.len() {
        let a = &pts[i - 1];
        let b = &pts[i];
        let seg = ((b.x - a.x).powi(2) + (b.y - a.y).powi(2)).sqrt();
        let steps = (seg / 0.5).ceil().max(1.0) as usize;
        for s in 0..=steps {
            let t = s as f64 / steps as f64;
            let x = a.x + (b.x - a.x) * t;
            let y = a.y + (b.y - a.y) * t;
            // Width interpolates from the segment's endpoint pressures.
            let p = a.p + (b.p - a.p) * t;
            stamp_disc(&mut mask, x, y, brush.width_for(p) / 2.0);
        }
    }
    Some(mask)
}

/// Composites a rasterised stroke into a strokes-layer buffer.
///
/// `Pen` uses source-over with the stroke's color and (opacity × coverage);
/// `Eraser` uses destination-out (lowers destination alpha by coverage),
/// so it only clears strokes and never touches the background (spec §4.4).
pub fn bake_stroke(layer: &mut RgbaBuffer, stroke: &Stroke, brush: &Brush, smoothing: Smoothing) {
    let Some(mask) = rasterize_free(stroke, brush, smoothing, layer.width, layer.height) else {
        return;
    };
    let (sr, sg, sb, sa) = unpack_rgba(stroke.color);
    let erase = stroke.tool == Tool::Eraser || stroke.erase;
    let base_alpha = (sa as f64 / 255.0) * stroke.opacity.clamp(0.0, 1.0);

    for ly in 0..mask.h {
        for lx in 0..mask.w {
            let cov = mask.cov[ly * mask.w + lx] as f64;
            if cov <= 0.0 {
                continue;
            }
            let gx = (mask.x0 as usize) + lx;
            let gy = (mask.y0 as usize) + ly;
            let i = layer.idx(gx as u32, gy as u32);

            if erase {
                // destination-out: keep = 1 - coverage.
                let keep = 1.0 - cov;
                for c in 0..4 {
                    layer.data[i + c] =
                        (layer.data[i + c] as f64 * keep).round().clamp(0.0, 255.0) as u8;
                }
                continue;
            }

            // source-over of a straight-alpha source onto a straight-alpha dst.
            let src_a = base_alpha * cov;
            if src_a <= 0.0 {
                continue;
            }
            let dst_a = layer.data[i + 3] as f64 / 255.0;
            let out_a = src_a + dst_a * (1.0 - src_a);
            let src = [sr, sg, sb];
            for (c, &sc) in src.iter().enumerate() {
                let s = sc as f64 / 255.0;
                let d = layer.data[i + c] as f64 / 255.0;
                let out = if out_a > 0.0 {
                    (s * src_a + d * dst_a * (1.0 - src_a)) / out_a
                } else {
                    0.0
                };
                layer.data[i + c] = (out * 255.0).round().clamp(0.0, 255.0) as u8;
            }
            layer.data[i + 3] = (out_a * 255.0).round().clamp(0.0, 255.0) as u8;
        }
    }
}

/// Convenience: the pixel `(r,g,b,a)` at a coordinate (for tests / inspection).
pub fn pixel_at(buf: &RgbaBuffer, x: u32, y: u32) -> (u8, u8, u8, u8) {
    let i = buf.idx(x, y);
    (
        buf.data[i],
        buf.data[i + 1],
        buf.data[i + 2],
        buf.data[i + 3],
    )
}

/// Re-bakes a whole strokes layer from scratch (used after undo/redo, spec
/// §6.2: "undo rebuilds the bake"). `params(stroke)` supplies the brush and
/// smoothing recorded for each stroke.
pub fn rebake_layer(
    width: u32,
    height: u32,
    strokes: &[Stroke],
    mut params: impl FnMut(&Stroke) -> (Brush, Smoothing),
) -> RgbaBuffer {
    let mut layer = RgbaBuffer::transparent(width, height);
    for s in strokes {
        let (brush, smoothing) = params(s);
        bake_stroke(&mut layer, s, &brush, smoothing);
    }
    layer
}

/// The flat (over-background) image: a clone of `background` with `layer`
/// composited over it.
pub fn flatten(background: &RgbaBuffer, layer: &RgbaBuffer) -> RgbaBuffer {
    let mut flat = background.clone();
    flat.composite_over(layer);
    flat
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::stroke::Point;

    fn pen_stroke(color: u32, size: f64, pts: &[(f64, f64, f64)]) -> Stroke {
        let mut s = Stroke::new(Tool::Pen, size, color);
        s.points = pts.iter().map(|&(x, y, p)| Point::new(x, y, p)).collect();
        s
    }

    #[test]
    fn pen_bakes_opaque_red_at_center() {
        let mut layer = RgbaBuffer::transparent(40, 40);
        let s = pen_stroke(0xFF0000FF, 10.0, &[(20.0, 20.0, 1.0)]);
        bake_stroke(&mut layer, &s, &Brush::from_size(10.0), Smoothing::Off);
        let (r, g, b, a) = pixel_at(&layer, 20, 20);
        assert_eq!((r, g, b), (255, 0, 0), "center is red");
        assert!(a > 250, "center is (near) opaque, got {a}");
    }

    #[test]
    fn transparent_outside_the_disc() {
        let mut layer = RgbaBuffer::transparent(40, 40);
        let s = pen_stroke(0xFF0000FF, 8.0, &[(20.0, 20.0, 1.0)]);
        bake_stroke(&mut layer, &s, &Brush::from_size(8.0), Smoothing::Off);
        let (_, _, _, a) = pixel_at(&layer, 0, 0);
        assert_eq!(a, 0, "far corner untouched");
    }

    #[test]
    fn lower_pressure_makes_a_narrower_stroke() {
        // Two horizontal strokes, one at low and one at high pressure.
        let brush = Brush::from_size(16.0);
        let mut thin = RgbaBuffer::transparent(60, 20);
        bake_stroke(
            &mut thin,
            &pen_stroke(0xFF0000FF, 16.0, &[(5.0, 10.0, 0.1), (55.0, 10.0, 0.1)]),
            &brush,
            Smoothing::Off,
        );
        let mut thick = RgbaBuffer::transparent(60, 20);
        bake_stroke(
            &mut thick,
            &pen_stroke(0xFF0000FF, 16.0, &[(5.0, 10.0, 1.0), (55.0, 10.0, 1.0)]),
            &brush,
            Smoothing::Off,
        );
        let count = |b: &RgbaBuffer| (0..b.height).filter(|&y| pixel_at(b, 30, y).3 > 10).count();
        assert!(
            count(&thick) > count(&thin),
            "high pressure must be wider: thin={}, thick={}",
            count(&thin),
            count(&thick)
        );
    }

    #[test]
    fn eraser_clears_only_strokes_not_background() {
        // Bake a pen stroke, then erase across it; flatten over a white bg.
        let mut layer = RgbaBuffer::transparent(40, 40);
        bake_stroke(
            &mut layer,
            &pen_stroke(0xFF0000FF, 14.0, &[(5.0, 20.0, 1.0), (35.0, 20.0, 1.0)]),
            &Brush::from_size(14.0),
            Smoothing::Off,
        );
        assert!(pixel_at(&layer, 20, 20).3 > 200, "stroke present first");

        let mut eraser = Stroke::new(Tool::Eraser, 20.0, 0x00000000);
        eraser.points = vec![Point::new(20.0, 20.0, 1.0)];
        bake_stroke(&mut layer, &eraser, &Brush::from_size(20.0), Smoothing::Off);
        assert!(pixel_at(&layer, 20, 20).3 < 10, "stroke erased");

        let white = RgbaBuffer::from_rgba(40, 40, vec![255; 40 * 40 * 4]);
        let flat = flatten(&white, &layer);
        assert_eq!(
            pixel_at(&flat, 20, 20),
            (255, 255, 255, 255),
            "background shows through the erased hole"
        );
    }

    #[test]
    fn flatten_composites_stroke_over_background() {
        let white = RgbaBuffer::from_rgba(20, 20, vec![255; 20 * 20 * 4]);
        let mut layer = RgbaBuffer::transparent(20, 20);
        bake_stroke(
            &mut layer,
            &pen_stroke(0xFF0000FF, 30.0, &[(10.0, 10.0, 1.0)]),
            &Brush::from_size(30.0),
            Smoothing::Off,
        );
        let flat = flatten(&white, &layer);
        assert_eq!(pixel_at(&flat, 10, 10), (255, 0, 0, 255), "red over white");
    }

    #[test]
    fn overlapping_stamps_in_one_stroke_do_not_darken() {
        // A semi-transparent stroke that doubles back on itself must not build
        // up alpha where it overlaps (single-path semantics via max coverage).
        let brush = Brush::from_size(12.0);
        let mut layer = RgbaBuffer::transparent(40, 40);
        let mut s = pen_stroke(
            0xFF000080,
            12.0,
            &[(20.0, 5.0, 1.0), (20.0, 35.0, 1.0), (20.0, 5.0, 1.0)],
        );
        s.opacity = 0.5;
        bake_stroke(&mut layer, &s, &brush, Smoothing::Off);
        let a = pixel_at(&layer, 20, 20).3 as f64;
        // color alpha 0x80/255 * opacity 0.5 ≈ 0.25 → ~64, not ~112.
        assert!(a < 90.0, "overlap must not accumulate alpha, got {a}");
    }

    #[test]
    fn rebake_matches_incremental_bake() {
        let brush = Brush::from_size(10.0);
        let strokes = vec![
            pen_stroke(0xFF0000FF, 10.0, &[(5.0, 5.0, 1.0), (25.0, 25.0, 0.5)]),
            pen_stroke(0x00FF00FF, 10.0, &[(25.0, 5.0, 0.8), (5.0, 25.0, 0.8)]),
        ];
        let mut incremental = RgbaBuffer::transparent(30, 30);
        for s in &strokes {
            bake_stroke(&mut incremental, s, &brush, Smoothing::Off);
        }
        let rebaked = rebake_layer(30, 30, &strokes, |_| (brush, Smoothing::Off));
        assert_eq!(incremental, rebaked, "rebake must equal incremental bake");
    }
}
