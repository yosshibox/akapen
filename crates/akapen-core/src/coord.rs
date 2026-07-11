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
}
