//! Pure `ViewTransform -> clip-space matrix` conversion (no GPU handle
//! needed, so this is unit-testable on its own — spec instruction "純ロジッ
//! ク…はコア or render内の純関数に切り出してテスト").
//!
//! Consumed by [`crate::background`] to place the background quad. The
//! background is drawn as a unit UV quad covering `[0,1]x[0,1]`; this module
//! builds the 4x4 matrix that carries a UV corner all the way to clip space:
//!
//! ```text
//! uv (0..1)  --(× buffer_w/buffer_h)-->  image-pixel space
//!            --(ViewTransform::image_to_screen, akapen_core oracle)-->  screen px (Y-down, top-left origin)
//!            --(viewport map)-->  WGSL clip space (Y-up, NDC [-1,1])
//! ```
//!
//! All three legs are affine (linear + translation), so the whole chain is
//! affine too; [`uv_to_clip_matrix`] reconstructs it from three sample
//! points (origin, +u, +v) rather than re-deriving the algebra by hand. This
//! keeps `akapen_core::coord::ViewTransform::image_to_screen` as the single
//! oracle for the zoom/pan/rotation math (spec §6.2 "この数式はmac/Windowsで
//! 同一でなければならない" — one implementation, not a re-derived copy).

use akapen_core::coord::{Point2, ViewTransform};

/// Builds the column-major 4x4 matrix (WGSL `mat4x4<f32>` layout: an array
/// of 4 columns) mapping a UV quad corner `(u, v)` in `[0,1]x[0,1]` — which
/// covers the whole `buffer_w x buffer_h` background image — to clip space,
/// for an `output_w x output_h` render target.
///
/// `output_w`/`output_h` are the pixel dimensions of the surface/offscreen
/// texture being rendered into; they are *not* necessarily equal to
/// `view.buffer_w`/`view.buffer_h` (e.g. the view can be zoomed so the
/// buffer only covers part of the output, or panned so it's off-center).
pub fn uv_to_clip_matrix(view: &ViewTransform, output_w: u32, output_h: u32) -> [[f32; 4]; 4] {
    let image_of = |u: f64, v: f64| Point2 {
        x: u * view.buffer_w,
        y: v * view.buffer_h,
    };
    let origin = ndc_of(view, output_w, output_h, image_of(0.0, 0.0));
    let along_u = ndc_of(view, output_w, output_h, image_of(1.0, 0.0));
    let along_v = ndc_of(view, output_w, output_h, image_of(0.0, 1.0));
    matrix_from_samples(origin, along_u, along_v)
}

/// Builds the same kind of clip-space matrix as [`uv_to_clip_matrix`], but for
/// vertices already expressed in canvas internal-buffer *pixel* space (not
/// UV `[0,1]`) — e.g. [`akapen_core::tessellate::Vertex::pos`]. The only
/// difference is skipping the `u * buffer_w` / `v * buffer_h` premultiplication
/// of the *input*; the same [`ViewTransform::image_to_screen`] (single
/// oracle, spec §6.2) still does the actual zoom/pan/rotation math, so a
/// stroke tessellated in image-pixel space lines up with the background
/// drawn through [`uv_to_clip_matrix`] under the same `view`.
pub fn image_to_clip_matrix(view: &ViewTransform, output_w: u32, output_h: u32) -> [[f32; 4]; 4] {
    let origin = ndc_of(view, output_w, output_h, Point2 { x: 0.0, y: 0.0 });
    let along_x = ndc_of(view, output_w, output_h, Point2 { x: 1.0, y: 0.0 });
    let along_y = ndc_of(view, output_w, output_h, Point2 { x: 0.0, y: 1.0 });
    matrix_from_samples(origin, along_x, along_y)
}

/// Screen pixels (Y-down, top-left origin, per akapen_core::coord's doc
/// comment) -> WGSL clip space (Y-up, NDC in [-1,1], top-left maps to
/// (-1, +1)).
fn ndc_of(view: &ViewTransform, output_w: u32, output_h: u32, p: Point2) -> (f32, f32) {
    let s = view.image_to_screen(p);
    (
        (2.0 * s.x / output_w as f64 - 1.0) as f32,
        (1.0 - 2.0 * s.y / output_h as f64) as f32,
    )
}

/// Affine map reconstructed from three samples (origin, +u, +v): columns are
/// the images of the u/v basis vectors (as displacements from the origin
/// sample), plus an untouched z column and the origin as translation. Shared
/// by [`uv_to_clip_matrix`] and [`image_to_clip_matrix`], which differ only
/// in what they sample.
fn matrix_from_samples(
    origin: (f32, f32),
    along_u: (f32, f32),
    along_v: (f32, f32),
) -> [[f32; 4]; 4] {
    [
        [along_u.0 - origin.0, along_u.1 - origin.1, 0.0, 0.0],
        [along_v.0 - origin.0, along_v.1 - origin.1, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [origin.0, origin.1, 0.0, 1.0],
    ]
}

/// A [`ViewTransform`] with no zoom, pan, or rotation: buffer pixel `(x, y)`
/// maps 1:1 to output pixel `(x, y)` when the render target is
/// `buffer_w x buffer_h`. Used to draw already-in-image-space geometry (e.g.
/// committed strokes baked into `baked_tex`, spec §6.2 point 3) at its
/// natural size, as opposed to the real on-screen [`ViewTransform`] used for
/// the final composite step.
pub fn identity_view(buffer_w: f64, buffer_h: f64) -> ViewTransform {
    ViewTransform {
        center_x: buffer_w / 2.0,
        center_y: buffer_h / 2.0,
        scale_x: 1.0,
        scale_y: 1.0,
        rotation_deg: 0.0,
        buffer_w,
        buffer_h,
    }
}

/// Applies a WGSL-layout column-major 4x4 matrix to a homogeneous point,
/// mirroring what the vertex shader does (`uniforms.transform * vec4(uv, 0,
/// 1)`). Used only by tests to check [`uv_to_clip_matrix`] without spinning
/// up a GPU.
#[cfg(test)]
fn apply(m: &[[f32; 4]; 4], x: f32, y: f32) -> (f32, f32) {
    // clip = m[0]*x + m[1]*y + m[2]*0 + m[3]*1
    let cx = m[0][0] * x + m[1][0] * y + m[3][0];
    let cy = m[0][1] * x + m[1][1] * y + m[3][1];
    (cx, cy)
}

#[cfg(test)]
mod tests {
    use super::*;

    const EPS: f32 = 1e-4;
    fn near(a: f32, b: f32, msg: &str) {
        assert!((a - b).abs() < EPS, "{msg}: expected {b}, got {a}");
    }

    fn identity_view(size: f64) -> ViewTransform {
        ViewTransform {
            center_x: size / 2.0,
            center_y: size / 2.0,
            scale_x: 1.0,
            scale_y: 1.0,
            rotation_deg: 0.0,
            buffer_w: size,
            buffer_h: size,
        }
    }

    #[test]
    fn identity_view_maps_uv_unit_square_to_full_clip_space() {
        let view = identity_view(256.0);
        let m = uv_to_clip_matrix(&view, 256, 256);

        // uv (0,0) is the image's top-left pixel corner -> screen top-left
        // -> clip (-1, +1) (Y flips: screen Y-down -> clip Y-up).
        let top_left = apply(&m, 0.0, 0.0);
        near(top_left.0, -1.0, "top-left x");
        near(top_left.1, 1.0, "top-left y");

        // uv (1,1) is the image's bottom-right corner -> clip (+1, -1).
        let bottom_right = apply(&m, 1.0, 1.0);
        near(bottom_right.0, 1.0, "bottom-right x");
        near(bottom_right.1, -1.0, "bottom-right y");

        // uv center -> clip origin.
        let center = apply(&m, 0.5, 0.5);
        near(center.0, 0.0, "center x");
        near(center.1, 0.0, "center y");
    }

    #[test]
    fn two_x_zoom_centered_doubles_clip_space_displacement_from_center() {
        // 2x zoom centered on the output magnifies the image: a point that
        // was already off toward one edge moves *twice as far* from the
        // clip-space center (the image's own center is the fixed point).
        // The image's actual corners can end up outside [-1,1] entirely
        // (correctly: they're now off-screen, the same as a real zoomed-in
        // viewer), so this checks displacement scaling at an interior point
        // rather than asserting the corners stay on-screen.
        let base = identity_view(256.0);
        let base_matrix = uv_to_clip_matrix(&base, 256, 256);
        let base_point = apply(&base_matrix, 0.75, 0.5);
        near(base_point.0, 0.5, "unzoomed reference x"); // sanity-check the baseline itself
        near(base_point.1, 0.0, "unzoomed reference y");

        let mut zoomed = base;
        zoomed.scale_x = 2.0;
        zoomed.scale_y = 2.0;
        let zoomed_matrix = uv_to_clip_matrix(&zoomed, 256, 256);
        let zoomed_point = apply(&zoomed_matrix, 0.75, 0.5);
        near(
            zoomed_point.0,
            2.0 * base_point.0,
            "zoomed x is double the displacement",
        );
        near(
            zoomed_point.1,
            2.0 * base_point.1,
            "zoomed y is double the displacement",
        );
    }

    #[test]
    fn pan_offsets_the_whole_quad_without_changing_its_size() {
        let mut view = identity_view(256.0);
        // Pan the view 32 output px to the right (screen +x).
        view.center_x += 32.0;
        let m = uv_to_clip_matrix(&view, 256, 256);

        let center = apply(&m, 0.5, 0.5);
        // 32px right in a 256px-wide output is +32/128 = 0.25 in NDC.
        near(center.0, 0.25, "panned center x");
        near(center.1, 0.0, "panned center y unaffected");
    }

    #[test]
    fn matches_image_to_screen_oracle_at_an_arbitrary_point() {
        // Cross-check against the core oracle directly (not just corners),
        // with a non-trivial view (asymmetric scale + rotation + pan),
        // matching the crate doc's "one implementation, not a re-derived
        // copy" intent.
        let view = ViewTransform {
            center_x: 300.0,
            center_y: 150.0,
            scale_x: 0.5,
            scale_y: 1.5,
            rotation_deg: 20.0,
            buffer_w: 400.0,
            buffer_h: 200.0,
        };
        let output_w = 640u32;
        let output_h = 360u32;
        let m = uv_to_clip_matrix(&view, output_w, output_h);

        for &(u, v) in &[(0.2, 0.7), (0.9, 0.1), (0.5, 0.5)] {
            let expected_screen = view.image_to_screen(Point2 {
                x: u * view.buffer_w,
                y: v * view.buffer_h,
            });
            let expected_clip = (
                (2.0 * expected_screen.x / output_w as f64 - 1.0) as f32,
                (1.0 - 2.0 * expected_screen.y / output_h as f64) as f32,
            );
            let got = apply(&m, u as f32, v as f32);
            near(got.0, expected_clip.0, "oracle x");
            near(got.1, expected_clip.1, "oracle y");
        }
    }

    // ── image_to_clip_matrix / identity_view (Phase c+d) ──

    #[test]
    fn identity_view_image_to_clip_maps_pixel_corners_like_uv_variant() {
        // Pixel-space (0,0)/(size,size)/(size/2,size/2) under
        // image_to_clip_matrix must land exactly where UV (0,0)/(1,1)/(0.5,0.5)
        // land under uv_to_clip_matrix for the same square identity view —
        // the two are the same affine map, just parameterized differently.
        let view = super::identity_view(256.0, 256.0);
        let m = image_to_clip_matrix(&view, 256, 256);

        let top_left = apply(&m, 0.0, 0.0);
        near(top_left.0, -1.0, "top-left x");
        near(top_left.1, 1.0, "top-left y");

        let bottom_right = apply(&m, 256.0, 256.0);
        near(bottom_right.0, 1.0, "bottom-right x");
        near(bottom_right.1, -1.0, "bottom-right y");

        let center = apply(&m, 128.0, 128.0);
        near(center.0, 0.0, "center x");
        near(center.1, 0.0, "center y");
    }

    #[test]
    fn image_to_clip_matrix_matches_image_to_screen_oracle() {
        // Same cross-check as `matches_image_to_screen_oracle_at_an_arbitrary_
        // point`, but for pixel-space input (no UV premultiplication) and a
        // non-square buffer, so image_to_clip_matrix stays independently
        // verified rather than just algebraically inferred from the UV
        // variant.
        let view = ViewTransform {
            center_x: 300.0,
            center_y: 150.0,
            scale_x: 0.5,
            scale_y: 1.5,
            rotation_deg: 20.0,
            buffer_w: 400.0,
            buffer_h: 200.0,
        };
        let output_w = 640u32;
        let output_h = 360u32;
        let m = image_to_clip_matrix(&view, output_w, output_h);

        for &(px, py) in &[(80.0, 140.0), (360.0, 20.0), (200.0, 100.0)] {
            let expected_screen = view.image_to_screen(Point2 { x: px, y: py });
            let expected_clip = (
                (2.0 * expected_screen.x / output_w as f64 - 1.0) as f32,
                (1.0 - 2.0 * expected_screen.y / output_h as f64) as f32,
            );
            let got = apply(&m, px as f32, py as f32);
            near(got.0, expected_clip.0, "oracle x");
            near(got.1, expected_clip.1, "oracle y");
        }
    }

    #[test]
    fn identity_view_helper_matches_local_square_identity_view() {
        // super::identity_view(w, h) must agree with this module's own
        // square-only test helper `identity_view(size)` above when w == h.
        let a = super::identity_view(128.0, 128.0);
        let b = identity_view(128.0);
        near(a.center_x as f32, b.center_x as f32, "center_x");
        near(a.center_y as f32, b.center_y as f32, "center_y");
        near(a.scale_x as f32, b.scale_x as f32, "scale_x");
        near(a.scale_y as f32, b.scale_y as f32, "scale_y");
    }
}
