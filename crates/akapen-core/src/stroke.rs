//! In-memory stroke model (spec §4.3 / §5.5).
//!
//! Pressure is **mandatory** on every point (spec §5.0 — the MUST / release
//! gate). `p` is the raw, pre-smoothing pressure in `0..=1`; the drawing
//! parameters (pressure curve, min/max width, opacity mapping) are applied at
//! render time and never bake pressure out of the stored point.

/// Drawing tool. Order/spelling matches the C ABI `Tool` enum (spec §7.2).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tool {
    Pen,
    Eraser,
    Line,
    Arrow,
    Rect,
    Ellipse,
    Text,
}

/// Origin of a pointer sample. Touch vs Pen matters for palm rejection and for
/// the "pressure not detected" guard (spec §5.2 / §5.4).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PointerKind {
    Pen,
    Touch,
    Mouse,
}

/// A single stroke sample. `p` (pressure) is required by construction.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Point {
    /// Canvas internal-buffer x (image-native pixels).
    pub x: f64,
    /// Canvas internal-buffer y (image-native pixels).
    pub y: f64,
    /// Raw pressure in `0..=1` (pre-smoothing). MUST be present (spec §5.5).
    pub p: f64,
}

impl Point {
    pub fn new(x: f64, y: f64, p: f64) -> Self {
        Point { x, y, p }
    }
}

/// One stroke: a tool, its style, and the pressure-bearing point list.
#[derive(Debug, Clone, PartialEq)]
pub struct Stroke {
    pub tool: Tool,
    pub points: Vec<Point>,
    /// Base brush size in px (nominal; pressure curve modulates around it).
    pub size: f64,
    /// Packed RGBA color, 0xRRGGBBAA.
    pub color: u32,
    /// Whether this stroke erases (composites in "clear" mode).
    pub erase: bool,
    /// Base opacity 0..=1.
    pub opacity: f64,
}

impl Stroke {
    pub fn new(tool: Tool, size: f64, color: u32) -> Self {
        Stroke {
            tool,
            points: Vec::new(),
            size,
            color,
            erase: false,
            opacity: 1.0,
        }
    }

    /// Whether pressure varies meaningfully across the stroke. Mirrors the
    /// "pressure not detected" guard (spec §5.4): a near-zero variance means
    /// the driver/Ink path is likely feeding a constant value.
    pub fn has_pressure_variation(&self, min_variance: f64) -> bool {
        if self.points.len() < 2 {
            return false;
        }
        let n = self.points.len() as f64;
        let mean = self.points.iter().map(|p| p.p).sum::<f64>() / n;
        let var = self
            .points
            .iter()
            .map(|p| (p.p - mean).powi(2))
            .sum::<f64>()
            / n;
        var > min_variance
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn constant_pressure_has_no_variation() {
        let mut s = Stroke::new(Tool::Pen, 6.0, 0xFF0000FF);
        for i in 0..5 {
            s.points.push(Point::new(i as f64, 0.0, 0.5));
        }
        assert!(!s.has_pressure_variation(1e-6));
    }

    #[test]
    fn varying_pressure_detected() {
        let mut s = Stroke::new(Tool::Pen, 6.0, 0xFF0000FF);
        for i in 0..5 {
            s.points.push(Point::new(i as f64, 0.0, 0.1 * i as f64));
        }
        assert!(s.has_pressure_variation(1e-6));
    }
}
