//! Coordinate transforms — ported from the VEDA reference implementation
//! `lib/annotate-geometry.js` (`clientToCanvasPoint`).
//!
//! Screen (client) coordinates -> annotation canvas internal-pixel
//! coordinates (inverse of the view transform). The caller computes the
//! displayed rect center, display scale (scaleX/scaleY) and rotation, and
//! passes them in; this pure function performs the inverse mapping.
//!
//! With rotation 0, center = rect center and scale = rect/buffer, this is
//! bit-for-bit equivalent to the "direct rect" formula used elsewhere in the
//! reference (`nx = (clientX - rect.left)/rect.width; x = nx * buffer`).
//! Its algorithm is the normative spec (see Akapen spec §7.3); the numeric
//! test vectors below are ported from `test/annotate-geometry.test.js`.

/// A 2D point in canvas internal-buffer pixel space.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Point2 {
    pub x: f64,
    pub y: f64,
}

/// Inputs to [`client_to_canvas_point`], mirroring the reference implementation.
#[derive(Debug, Clone, Copy)]
pub struct ClientToCanvasInput {
    pub client_x: f64,
    pub client_y: f64,
    pub center_x: f64,
    pub center_y: f64,
    pub scale_x: f64,
    pub scale_y: f64,
    pub rotation_deg: f64,
    pub buffer_w: f64,
    pub buffer_h: f64,
}

/// Inverse-maps a screen (client) point to canvas internal-buffer pixels.
///
/// Equivalent to `clientToCanvasPoint` in `lib/annotate-geometry.js`.
pub fn client_to_canvas_point(input: ClientToCanvasInput) -> Point2 {
    let dx = input.client_x - input.center_x;
    let dy = input.client_y - input.center_y;
    let rad = (-input.rotation_deg) * std::f64::consts::PI / 180.0;
    let cos = rad.cos();
    let sin = rad.sin();
    let rx = dx * cos - dy * sin;
    let ry = dx * sin + dy * cos;
    // Match the JS `scaleX || 1` fallback for zero/NaN-ish scales.
    let sx = if input.scale_x != 0.0 {
        input.scale_x
    } else {
        1.0
    };
    let sy = if input.scale_y != 0.0 {
        input.scale_y
    } else {
        1.0
    };
    Point2 {
        x: input.buffer_w / 2.0 + rx / sx,
        y: input.buffer_h / 2.0 + ry / sy,
    }
}

/// The view (display) transform between canvas internal-buffer pixels
/// (image-native space) and screen (client) pixels: display center, per-axis
/// scale (rendered-rect / buffer), rotation, and the buffer's own size.
///
/// This is the forward counterpart to [`client_to_canvas_point`]/
/// [`ClientToCanvasInput`] — same parameters, opposite direction. It exists so
/// a GPU consumer (spec §7.2 `render(surface, view: ViewTransform)`) can place
/// the baked texture and the wet-ink stroke on screen without re-deriving the
/// inverse math. Kept in the core (not the shell) per spec §6.2's "B22 教訓"
/// note: this math must be identical on mac and Windows.
#[derive(Debug, Clone, Copy)]
pub struct ViewTransform {
    /// Screen-space x/y of the displayed rect's center.
    pub center_x: f64,
    pub center_y: f64,
    /// Displayed-rect-size / buffer-size, per axis (zoom folded in).
    pub scale_x: f64,
    pub scale_y: f64,
    pub rotation_deg: f64,
    /// Canvas internal-buffer size (image-native pixels).
    pub buffer_w: f64,
    pub buffer_h: f64,
}

impl ViewTransform {
    /// Forward-maps a canvas internal-buffer point to a screen (client)
    /// point. Exact inverse of [`Self::screen_to_image`] (same parameters),
    /// modulo floating-point round-trip error.
    pub fn image_to_screen(&self, p: Point2) -> Point2 {
        let sx = if self.scale_x != 0.0 {
            self.scale_x
        } else {
            1.0
        };
        let sy = if self.scale_y != 0.0 {
            self.scale_y
        } else {
            1.0
        };
        let rx = (p.x - self.buffer_w / 2.0) * sx;
        let ry = (p.y - self.buffer_h / 2.0) * sy;
        // Undo the inverse's `rad = -rotation_deg` rotation: rotate by
        // `+rotation_deg` here (rotation matrices are orthogonal, so the
        // inverse of R(-theta) is R(theta)).
        let rad = self.rotation_deg * std::f64::consts::PI / 180.0;
        let cos = rad.cos();
        let sin = rad.sin();
        let dx = rx * cos - ry * sin;
        let dy = rx * sin + ry * cos;
        Point2 {
            x: self.center_x + dx,
            y: self.center_y + dy,
        }
    }

    /// Inverse-maps a screen (client) point to canvas internal-buffer pixels.
    /// Thin wrapper over [`client_to_canvas_point`] using this transform's
    /// fields (kept as a free function too, since it is the ported-1:1
    /// reference algorithm, spec §7.3).
    pub fn screen_to_image(&self, p: Point2) -> Point2 {
        client_to_canvas_point(ClientToCanvasInput {
            client_x: p.x,
            client_y: p.y,
            center_x: self.center_x,
            center_y: self.center_y,
            scale_x: self.scale_x,
            scale_y: self.scale_y,
            rotation_deg: self.rotation_deg,
            buffer_w: self.buffer_w,
            buffer_h: self.buffer_h,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // EPS matches the reference test suite (`Math.abs(a-b) < 1e-6`).
    const EPS: f64 = 1e-6;
    fn near(a: f64, b: f64, msg: &str) {
        assert!((a - b).abs() < EPS, "{msg}: expected {b}, got {a}");
    }

    // Ported 1:1 from test/annotate-geometry.test.js.

    #[test]
    fn center_click_returns_buffer_center_regardless_of_scale() {
        let buffer_w = 1920.0;
        let buffer_h = 1080.0;
        let p = client_to_canvas_point(ClientToCanvasInput {
            client_x: 500.0,
            client_y: 300.0,
            center_x: 500.0,
            center_y: 300.0,
            scale_x: 3.7,
            scale_y: 3.7,
            rotation_deg: 0.0,
            buffer_w,
            buffer_h,
        });
        near(p.x, buffer_w / 2.0, "x");
        near(p.y, buffer_h / 2.0, "y");
    }

    #[test]
    fn top_left_corner_click_is_origin() {
        let buffer_w = 800.0;
        let buffer_h = 600.0;
        let rect_w = 400.0;
        let rect_h = 300.0;
        let center_x = 200.0;
        let center_y = 150.0;
        let p = client_to_canvas_point(ClientToCanvasInput {
            client_x: center_x - rect_w / 2.0,
            client_y: center_y - rect_h / 2.0,
            center_x,
            center_y,
            scale_x: rect_w / buffer_w,
            scale_y: rect_h / buffer_h,
            rotation_deg: 0.0,
            buffer_w,
            buffer_h,
        });
        near(p.x, 0.0, "x");
        near(p.y, 0.0, "y");
    }

    #[test]
    fn fit_scale_case_display_width_double_buffer() {
        let buffer_w = 1000.0;
        let buffer_h = 1000.0;
        let center_x = 640.0;
        let center_y = 360.0;
        let p = client_to_canvas_point(ClientToCanvasInput {
            client_x: center_x + 100.0,
            client_y: center_y,
            center_x,
            center_y,
            scale_x: 2.0,
            scale_y: 2.0,
            rotation_deg: 0.0,
            buffer_w,
            buffer_h,
        });
        near(p.x, buffer_w / 2.0 + 50.0, "x");
        near(p.y, buffer_h / 2.0, "y");
    }

    #[test]
    fn rotation_90_maps_rightward_to_upward() {
        let buffer_w = 200.0;
        let buffer_h = 200.0;
        let p = client_to_canvas_point(ClientToCanvasInput {
            client_x: 100.0 + 40.0,
            client_y: 100.0,
            center_x: 100.0,
            center_y: 100.0,
            scale_x: 1.0,
            scale_y: 1.0,
            rotation_deg: 90.0,
            buffer_w,
            buffer_h,
        });
        near(p.x, buffer_w / 2.0, "x");
        near(p.y, buffer_h / 2.0 - 40.0, "y");
    }

    #[test]
    fn rotation_0_matches_direct_rect_formula() {
        let buffer_w = 640.0;
        let buffer_h = 480.0;
        let rect_left = 50.0;
        let rect_top = 20.0;
        let rect_w = 320.0;
        let rect_h = 240.0;
        let client_x = 210.0;
        let client_y = 140.0;
        // "direct rect" reference formula
        let nx = (client_x - rect_left) / rect_w;
        let ny = (client_y - rect_top) / rect_h;
        let media = Point2 {
            x: nx * buffer_w,
            y: ny * buffer_h,
        };
        // geometry formula
        let p = client_to_canvas_point(ClientToCanvasInput {
            client_x,
            client_y,
            center_x: rect_left + rect_w / 2.0,
            center_y: rect_top + rect_h / 2.0,
            scale_x: rect_w / buffer_w,
            scale_y: rect_h / buffer_h,
            rotation_deg: 0.0,
            buffer_w,
            buffer_h,
        });
        near(p.x, media.x, "x match");
        near(p.y, media.y, "y match");
    }

    // Extra: DPR (device-pixel-ratio) coverage. A buffer sized at DPR*css and a
    // display scale that folds DPR in still lands center-on-center and scales
    // displacements by the combined factor.
    #[test]
    fn dpr_scaled_buffer_maps_consistently() {
        let dpr = 2.0;
        let css_w = 500.0;
        let css_h = 500.0;
        let buffer_w = css_w * dpr; // 1000 device px
        let buffer_h = css_h * dpr;
        // Displayed at 1:1 CSS pixels => scale = cssRect/buffer = 1/dpr.
        let center_x = 400.0;
        let center_y = 400.0;
        let p = client_to_canvas_point(ClientToCanvasInput {
            client_x: center_x + 30.0, // 30 css px right of center
            client_y: center_y,
            center_x,
            center_y,
            scale_x: 1.0 / dpr,
            scale_y: 1.0 / dpr,
            rotation_deg: 0.0,
            buffer_w,
            buffer_h,
        });
        // 30 css px => 30*dpr device px in buffer space.
        near(p.x, buffer_w / 2.0 + 30.0 * dpr, "x");
        near(p.y, buffer_h / 2.0, "y");
    }

    // ── ViewTransform (forward) ──

    fn near_p(a: Point2, b: Point2, msg: &str) {
        near(a.x, b.x, &format!("{msg} x"));
        near(a.y, b.y, &format!("{msg} y"));
    }

    #[test]
    fn forward_center_of_buffer_maps_to_screen_center() {
        let view = ViewTransform {
            center_x: 500.0,
            center_y: 300.0,
            scale_x: 3.7,
            scale_y: 3.7,
            rotation_deg: 0.0,
            buffer_w: 1920.0,
            buffer_h: 1080.0,
        };
        let p = view.image_to_screen(Point2 {
            x: view.buffer_w / 2.0,
            y: view.buffer_h / 2.0,
        });
        near_p(p, Point2 { x: 500.0, y: 300.0 }, "center");
    }

    #[test]
    fn forward_and_inverse_round_trip_through_screen() {
        // Screen point -> image point -> screen point must return the
        // original screen point (with rotation and non-uniform scale).
        let view = ViewTransform {
            center_x: 640.0,
            center_y: 360.0,
            scale_x: 0.5,
            scale_y: 0.75,
            rotation_deg: 37.0,
            buffer_w: 1000.0,
            buffer_h: 800.0,
        };
        for &(cx, cy) in &[(700.0, 400.0), (0.0, 0.0), (640.0, 360.0), (-200.0, 900.0)] {
            let screen_in = Point2 { x: cx, y: cy };
            let image = view.screen_to_image(screen_in);
            let screen_out = view.image_to_screen(image);
            near_p(screen_out, screen_in, "round trip");
        }
    }

    #[test]
    fn forward_and_inverse_round_trip_through_image() {
        // Image point -> screen point -> image point must return the
        // original image point.
        let view = ViewTransform {
            center_x: 200.0,
            center_y: 150.0,
            scale_x: 2.2,
            scale_y: 1.1,
            rotation_deg: -64.0,
            buffer_w: 3840.0,
            buffer_h: 2160.0,
        };
        for &(ix, iy) in &[
            (0.0, 0.0),
            (3840.0, 2160.0),
            (1920.0, 1080.0),
            (10.0, 2000.0),
        ] {
            let image_in = Point2 { x: ix, y: iy };
            let screen = view.image_to_screen(image_in);
            let image_out = view.screen_to_image(screen);
            near_p(image_out, image_in, "round trip");
        }
    }

    #[test]
    fn forward_matches_direct_rect_formula_at_rotation_zero() {
        // Mirror of `rotation_0_matches_direct_rect_formula`, forward
        // direction: buffer-space point -> screen point via the direct rect
        // formula `screen = rect.topLeft + (buffer_point/buffer) * rect_size`.
        let buffer_w = 640.0;
        let buffer_h = 480.0;
        let rect_left = 50.0;
        let rect_top = 20.0;
        let rect_w = 320.0;
        let rect_h = 240.0;
        let image_x = 400.0;
        let image_y = 100.0;
        let expect_x = rect_left + (image_x / buffer_w) * rect_w;
        let expect_y = rect_top + (image_y / buffer_h) * rect_h;

        let view = ViewTransform {
            center_x: rect_left + rect_w / 2.0,
            center_y: rect_top + rect_h / 2.0,
            scale_x: rect_w / buffer_w,
            scale_y: rect_h / buffer_h,
            rotation_deg: 0.0,
            buffer_w,
            buffer_h,
        };
        let p = view.image_to_screen(Point2 {
            x: image_x,
            y: image_y,
        });
        near(p.x, expect_x, "x match");
        near(p.y, expect_y, "y match");
    }

    #[test]
    fn forward_rotation_90_maps_upward_to_rightward() {
        // Inverse counterpart of `rotation_90_maps_rightward_to_upward`: a
        // point above the buffer center must land to the right of the
        // screen center after a +90 degree rotation.
        let buffer_w = 200.0;
        let buffer_h = 200.0;
        let view = ViewTransform {
            center_x: 100.0,
            center_y: 100.0,
            scale_x: 1.0,
            scale_y: 1.0,
            rotation_deg: 90.0,
            buffer_w,
            buffer_h,
        };
        let p = view.image_to_screen(Point2 {
            x: buffer_w / 2.0,
            y: buffer_h / 2.0 - 40.0,
        });
        near(p.x, 100.0 + 40.0, "x");
        near(p.y, 100.0, "y");
    }

    // B22 regression, forward side (spec §6.2 "B22 の教訓を回帰項目化"): the
    // root cause was a layout bug that silently dropped the displayed rect's
    // left/top offset, snapping the drawing surface back to the container's
    // origin instead of the actual (off-origin) rendered rect. Guard that the
    // forward transform never collapses a non-zero rect origin: the image's
    // top-left corner must map to the *rect's* top-left corner, not (0,0) and
    // not the container/viewport origin.
    #[test]
    fn forward_origin_does_not_collapse_to_container_origin_b22() {
        let buffer_w = 3840.0; // 4K natural width (spec §6 B24/B22 scenario)
        let buffer_h = 2160.0;
        // Rect is off-origin within a larger container (e.g. centered in a
        // viewport that is itself scrolled/offset) — not anchored at (0,0).
        let rect_left = 137.0;
        let rect_top = 64.0;
        let rect_w = 960.0;
        let rect_h = 540.0;
        let view = ViewTransform {
            center_x: rect_left + rect_w / 2.0,
            center_y: rect_top + rect_h / 2.0,
            scale_x: rect_w / buffer_w,
            scale_y: rect_h / buffer_h,
            rotation_deg: 0.0,
            buffer_w,
            buffer_h,
        };
        let top_left = view.image_to_screen(Point2 { x: 0.0, y: 0.0 });
        // A regressed implementation that drops center_x/center_y (or
        // silently treats the rect as anchored at the container origin)
        // would report (0,0) here instead of the true rect corner.
        near(top_left.x, rect_left, "left edge must not collapse to 0");
        near(top_left.y, rect_top, "top edge must not collapse to 0");
        assert!(
            top_left.x != 0.0 && top_left.y != 0.0,
            "B22 regression guard"
        );
    }
}
