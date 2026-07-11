//! Stroke tessellation — pure `stroke + brush + smoothing -> vertex list`
//! (spec §7.2 / §6.2). This is the geometry both the future GPU path (wgpu,
//! spec §7.4) and, if ever needed, a CPU fallback are meant to consume: a
//! flat `TriangleList` (every 3 [`Vertex`] entries form one triangle), built
//! from tapered quads along each segment plus round fans at every point
//! (caps at the ends, joins everywhere else — spec wording "三角形帯 +
//! 丸キャップ/ジョイン").
//!
//! Width comes from the existing [`crate::brush::Brush::width_for`] (same
//! pressure→width mapping as `raster.rs`); smoothing reuses
//! [`crate::smoothing::smooth_points`] (coordinates only — per-point
//! pressure `p` is carried through untouched, spec §5.3/§5.5).
//!
//! This module deliberately does **not** try to reproduce `raster.rs`'s
//! pixel-exact antialiasing. `raster.rs` remains the correctness oracle for
//! pixel output; `tessellate.rs` only has to produce geometry whose bounding
//! rectangle is a safe superset of what `raster.rs` paints (see the oracle
//! test below), so a GPU consumer never clips a pixel the CPU path would
//! have drawn.

use crate::brush::Brush;
use crate::smoothing::{smooth_points, Smoothing};
use crate::stroke::Stroke;

/// Number of segments used to approximate a circle (caps/joins). Chosen for
/// visual smoothness at typical brush sizes; not part of any external
/// contract, so it may change without notice.
const CIRCLE_SEGMENTS: usize = 16;

/// Extra radius (px) added on top of the brush half-width when placing
/// vertices, mirroring `raster.rs`'s `stamp_disc` antialiasing ramp (which
/// reaches zero coverage at `radius + 0.5`, not at `radius`) plus one more
/// half-pixel of slack. The extra slack matters because `raster.rs`'s
/// "non-zero pixel" is a whole *pixel column/row* (sampled at its center),
/// not a continuous boundary: a pixel can register non-zero alpha from
/// coverage that only touches part of it, up to half a pixel beyond the
/// `radius + 0.5` continuous boundary. Without this margin, tessellated
/// geometry could clip a pixel `raster.rs` would have painted; see the
/// bbox-superset oracle test below.
const AA_MARGIN: f64 = 1.0;

/// One tessellated vertex: a position in canvas internal-buffer pixel space
/// (image-native, the same space `Stroke`/`Point` use). `TriangleList`
/// topology — consume 3 at a time.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Vertex {
    pub pos: [f32; 2],
}

impl Vertex {
    fn new(x: f64, y: f64) -> Self {
        Vertex {
            pos: [x as f32, y as f32],
        }
    }
}

/// Tessellates one stroke into a flat triangle list.
///
/// - Smooths point *coordinates* only (`smooth_points`), matching the CPU
///   bake path (`raster.rs::rasterize_free`).
/// - A single-point stroke (tap) yields one circular fan.
/// - Each consecutive point pair yields a tapered quad (2 triangles) whose
///   half-widths come from `brush.width_for(pressure)`, plus a circular fan
///   at every point (endpoints = round caps, interior points = round
///   joins).
///
/// Returns an empty vec for an empty stroke.
pub fn tessellate_stroke(stroke: &Stroke, brush: &Brush, smoothing: Smoothing) -> Vec<Vertex> {
    let mut out = Vec::new();
    if stroke.points.is_empty() {
        return out;
    }
    let pts = smooth_points(&stroke.points, smoothing);
    let radius = |p: f64| brush.width_for(p) / 2.0 + AA_MARGIN;

    if pts.len() == 1 {
        let p = &pts[0];
        push_circle(&mut out, p.x, p.y, radius(p.p));
        return out;
    }

    for w in pts.windows(2) {
        let a = w[0];
        let b = w[1];
        push_quad(&mut out, a.x, a.y, radius(a.p), b.x, b.y, radius(b.p));
    }
    for p in &pts {
        push_circle(&mut out, p.x, p.y, radius(p.p));
    }
    out
}

/// Appends a tapered quad (2 triangles) connecting the perpendicular offsets
/// of `radius_a` at `a` and `radius_b` at `b`. No-op for a degenerate
/// (zero-length) segment — its endpoints are still covered by the per-point
/// circle fans.
#[allow(clippy::too_many_arguments)]
fn push_quad(out: &mut Vec<Vertex>, ax: f64, ay: f64, ra: f64, bx: f64, by: f64, rb: f64) {
    let dx = bx - ax;
    let dy = by - ay;
    let len = (dx * dx + dy * dy).sqrt();
    if len < 1e-9 {
        return;
    }
    // Unit perpendicular (rotate the direction vector 90 degrees).
    let nx = -dy / len;
    let ny = dx / len;

    let p1 = (ax + nx * ra, ay + ny * ra);
    let p2 = (bx + nx * rb, by + ny * rb);
    let p3 = (bx - nx * rb, by - ny * rb);
    let p4 = (ax - nx * ra, ay - ny * ra);

    push_triangle(out, p1, p2, p3);
    push_triangle(out, p1, p3, p4);
}

fn push_triangle(out: &mut Vec<Vertex>, a: (f64, f64), b: (f64, f64), c: (f64, f64)) {
    out.push(Vertex::new(a.0, a.1));
    out.push(Vertex::new(b.0, b.1));
    out.push(Vertex::new(c.0, c.1));
}

/// Appends a full circle as a [`CIRCLE_SEGMENTS`]-triangle fan around
/// `(cx, cy)`.
fn push_circle(out: &mut Vec<Vertex>, cx: f64, cy: f64, r: f64) {
    let tau = std::f64::consts::TAU;
    for i in 0..CIRCLE_SEGMENTS {
        let a0 = tau * (i as f64) / (CIRCLE_SEGMENTS as f64);
        let a1 = tau * ((i + 1) as f64) / (CIRCLE_SEGMENTS as f64);
        let p0 = (cx + r * a0.cos(), cy + r * a0.sin());
        let p1 = (cx + r * a1.cos(), cy + r * a1.sin());
        push_triangle(out, (cx, cy), p0, p1);
    }
}

/// Axis-aligned bounding box `(min_x, min_y, max_x, max_y)` of a vertex
/// list. Returns `None` for an empty list.
pub fn bounding_box(verts: &[Vertex]) -> Option<(f32, f32, f32, f32)> {
    if verts.is_empty() {
        return None;
    }
    let mut min_x = f32::MAX;
    let mut min_y = f32::MAX;
    let mut max_x = f32::MIN;
    let mut max_y = f32::MIN;
    for v in verts {
        min_x = min_x.min(v.pos[0]);
        min_y = min_y.min(v.pos[1]);
        max_x = max_x.max(v.pos[0]);
        max_y = max_y.max(v.pos[1]);
    }
    Some((min_x, min_y, max_x, max_y))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::raster::{bake_stroke, pixel_at, RgbaBuffer};
    use crate::stroke::{Point, Tool};

    fn pen_stroke(size: f64, pts: &[(f64, f64, f64)]) -> Stroke {
        let mut s = Stroke::new(Tool::Pen, size, 0xFF0000FF);
        s.points = pts.iter().map(|&(x, y, p)| Point::new(x, y, p)).collect();
        s
    }

    #[test]
    fn empty_stroke_yields_no_vertices() {
        let s = Stroke::new(Tool::Pen, 10.0, 0xFF0000FF);
        let brush = Brush::from_size(10.0);
        assert!(tessellate_stroke(&s, &brush, Smoothing::Off).is_empty());
    }

    #[test]
    fn triangle_list_is_well_formed() {
        let s = pen_stroke(10.0, &[(0.0, 0.0, 0.5), (10.0, 0.0, 0.8), (20.0, 5.0, 0.3)]);
        let brush = Brush::from_size(10.0);
        let verts = tessellate_stroke(&s, &brush, Smoothing::Off);
        assert!(!verts.is_empty());
        assert_eq!(verts.len() % 3, 0, "must be a flat triangle list");
    }

    #[test]
    fn single_point_stroke_yields_one_circle_fan() {
        let s = pen_stroke(10.0, &[(5.0, 5.0, 1.0)]);
        let brush = Brush::from_size(10.0);
        let verts = tessellate_stroke(&s, &brush, Smoothing::Off);
        assert_eq!(verts.len(), CIRCLE_SEGMENTS * 3);
    }

    #[test]
    fn higher_pressure_makes_a_wider_bounding_box() {
        // Mirrors raster.rs's `lower_pressure_makes_a_narrower_stroke`.
        let brush = Brush::from_size(16.0);
        let thin = pen_stroke(16.0, &[(5.0, 10.0, 0.1), (55.0, 10.0, 0.1)]);
        let thick = pen_stroke(16.0, &[(5.0, 10.0, 1.0), (55.0, 10.0, 1.0)]);
        let (_, ty0, _, ty1) =
            bounding_box(&tessellate_stroke(&thin, &brush, Smoothing::Off)).unwrap();
        let (_, ky0, _, ky1) =
            bounding_box(&tessellate_stroke(&thick, &brush, Smoothing::Off)).unwrap();
        let thin_h = ty1 - ty0;
        let thick_h = ky1 - ky0;
        assert!(
            thick_h > thin_h,
            "high pressure must be wider: thin={thin_h}, thick={thick_h}"
        );
    }

    #[test]
    fn width_widens_monotonically_with_brush_size() {
        let s = pen_stroke(10.0, &[(0.0, 0.0, 1.0), (30.0, 0.0, 1.0)]);
        let small = Brush::from_size(4.0);
        let large = Brush::from_size(40.0);
        let (_, sy0, _, sy1) =
            bounding_box(&tessellate_stroke(&s, &small, Smoothing::Off)).unwrap();
        let (_, ly0, _, ly1) =
            bounding_box(&tessellate_stroke(&s, &large, Smoothing::Off)).unwrap();
        assert!((ly1 - ly0) > (sy1 - sy0), "larger brush must be wider");
    }

    #[test]
    fn smoothing_preserves_pressure_driven_width() {
        // Smoothing must only move coordinates, never flatten the
        // pressure->width mapping (spec §5.3). A high-pressure smoothed
        // stroke must still be wider than a low-pressure one.
        let brush = Brush::from_size(20.0);
        let thin = pen_stroke(
            20.0,
            &[
                (0.0, 0.0, 0.1),
                (10.0, 2.0, 0.1),
                (20.0, -2.0, 0.1),
                (30.0, 0.0, 0.1),
                (40.0, 0.0, 0.1),
            ],
        );
        let thick = pen_stroke(
            20.0,
            &[
                (0.0, 0.0, 1.0),
                (10.0, 2.0, 1.0),
                (20.0, -2.0, 1.0),
                (30.0, 0.0, 1.0),
                (40.0, 0.0, 1.0),
            ],
        );
        let (_, ty0, _, ty1) =
            bounding_box(&tessellate_stroke(&thin, &brush, Smoothing::Strong)).unwrap();
        let (_, ky0, _, ky1) =
            bounding_box(&tessellate_stroke(&thick, &brush, Smoothing::Strong)).unwrap();
        assert!((ky1 - ky0) > (ty1 - ty0));
    }

    /// Oracle test (spec instruction): for the same stroke/brush/smoothing,
    /// the tessellated vertices' bounding rectangle must be a superset of
    /// `raster.rs`'s non-zero-alpha pixel bounding rectangle.
    #[test]
    fn tessellated_bbox_covers_raster_nonzero_pixels() {
        let brush = Brush::from_size(14.0);
        let stroke = pen_stroke(
            14.0,
            &[(8.0, 30.0, 0.2), (25.0, 10.0, 0.9), (45.0, 35.0, 0.5)],
        );

        let mut layer = RgbaBuffer::transparent(60, 60);
        bake_stroke(&mut layer, &stroke, &brush, Smoothing::Off);
        let mut raster_bbox: Option<(i64, i64, i64, i64)> = None;
        for y in 0..layer.height {
            for x in 0..layer.width {
                if pixel_at(&layer, x, y).3 > 0 {
                    let (x, y) = (x as i64, y as i64);
                    raster_bbox = Some(match raster_bbox {
                        None => (x, y, x, y),
                        Some((x0, y0, x1, y1)) => (x0.min(x), y0.min(y), x1.max(x), y1.max(y)),
                    });
                }
            }
        }
        let (rx0, ry0, rx1, ry1) = raster_bbox.expect("raster must have painted something");

        let verts = tessellate_stroke(&stroke, &brush, Smoothing::Off);
        let (vx0, vy0, vx1, vy1) = bounding_box(&verts).expect("tessellate must emit vertices");

        assert!(
            (vx0 as f64) <= rx0 as f64
                && (vy0 as f64) <= ry0 as f64
                && (vx1 as f64) >= (rx1 + 1) as f64
                && (vy1 as f64) >= (ry1 + 1) as f64,
            "tessellate bbox [{vx0},{vy0},{vx1},{vy1}] must cover raster bbox [{rx0},{ry0},{rx1},{ry1}]"
        );
    }
}
