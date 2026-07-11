//! The drawing engine — the stateful heart the shells drive (spec §7.2).
//!
//! It owns the background bitmap, the incrementally-baked strokes layer, the
//! committed stroke history (with undo/redo), and the in-progress stroke. It
//! consumes **normalized pointer samples only** (pressure required) and knows
//! nothing about any UI toolkit (spec §7.1). Free-hand pen + eraser are the M1
//! tools; shapes/text are later milestones (spec §9 M5 / roadmap).

use crate::brush::{Brush, PressureCurve};
use crate::export::{encode_png, stroke_to_doc, ExportSet};
use crate::raster::{bake_stroke, flatten, rebake_layer, RgbaBuffer};
use crate::smoothing::Smoothing;
use crate::stroke::{Point, PointerKind, Stroke, Tool};
use crate::vector::VectorDoc;

/// Pointer sample phase (spec §7.2 `Phase`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Phase {
    Down,
    Move,
    Up,
}

/// A normalized pointer sample (spec §7.2). Pressure is **required** (`0..=1`).
/// The shell converts NSEvent / WM_POINTER / Wintab into this; the core never
/// sees the OS path (spec §5.1).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PointerSample {
    /// Canvas internal-buffer x (image-native pixels).
    pub x: f64,
    /// Canvas internal-buffer y (image-native pixels).
    pub y: f64,
    /// Raw pressure `0..=1`. Mouse input passes `1.0` (constant width).
    pub pressure: f64,
    pub kind: PointerKind,
    pub phase: Phase,
}

/// A committed stroke together with the brush/smoothing it was drawn with, so
/// the layer can be rebaked after undo (spec §6.2).
struct Committed {
    stroke: Stroke,
    brush: Brush,
    smoothing: Smoothing,
}

/// The drawing engine.
pub struct Engine {
    background: RgbaBuffer,
    layer: RgbaBuffer,
    committed: Vec<Committed>,
    redo: Vec<Committed>,
    current: Option<Stroke>,

    // Current tool state.
    tool: Tool,
    color: u32,
    size: f64,
    opacity: f64,
    brush: Brush,
    smoothing: Smoothing,

    /// Threshold for the "pressure not detected" guard (spec §5.4).
    pub pressure_variance_threshold: f64,
    /// Set true whenever a committed pen stroke had no pressure variation, so
    /// the shell can surface the §5.4 warning instead of silently drawing flat.
    pub pressure_stuck_warning: bool,
}

impl Engine {
    /// New engine over an opaque white background of the given natural size.
    pub fn new(natural_w: u32, natural_h: u32) -> Self {
        let background = RgbaBuffer::from_rgba(
            natural_w,
            natural_h,
            vec![255; (natural_w as usize) * (natural_h as usize) * 4],
        );
        Self::with_background(background)
    }

    /// New engine over a decoded RGBA background (spec §7.2 `set_background`).
    pub fn from_rgba(rgba: Vec<u8>, natural_w: u32, natural_h: u32) -> Self {
        Self::with_background(RgbaBuffer::from_rgba(natural_w, natural_h, rgba))
    }

    fn with_background(background: RgbaBuffer) -> Self {
        let (w, h) = (background.width, background.height);
        Engine {
            background,
            layer: RgbaBuffer::transparent(w, h),
            committed: Vec::new(),
            redo: Vec::new(),
            current: None,
            tool: Tool::Pen,
            color: 0xFF0000FF, // red
            size: 6.0,
            opacity: 1.0,
            brush: Brush::from_size(6.0),
            smoothing: Smoothing::Off,
            pressure_variance_threshold: 1e-4,
            pressure_stuck_warning: false,
        }
    }

    pub fn natural_size(&self) -> (u32, u32) {
        (self.background.width, self.background.height)
    }

    // --- tool / style setters (spec §7.2) ---

    pub fn set_tool(&mut self, tool: Tool) {
        self.tool = tool;
    }
    pub fn set_color(&mut self, rgba: u32) {
        self.color = rgba;
    }
    pub fn set_size(&mut self, px: f32) {
        self.size = px as f64;
        let curve = self.brush.curve;
        let ofp = self.brush.opacity_from_pressure;
        self.brush = Brush::from_size(self.size);
        self.brush.curve = curve;
        self.brush.opacity_from_pressure = ofp;
    }
    pub fn set_opacity(&mut self, pct: f32) {
        self.opacity = (pct as f64 / 100.0).clamp(0.0, 1.0);
    }
    pub fn set_pressure_curve(&mut self, c: PressureCurve) {
        self.brush.curve = c;
    }
    pub fn set_opacity_from_pressure(&mut self, on: bool) {
        self.brush.opacity_from_pressure = on;
    }
    pub fn set_smoothing(&mut self, s: Smoothing) {
        self.smoothing = s;
    }

    /// True when there is an in-progress or committed stroke that would be lost
    /// on navigation (spec §4.5 unsaved guard).
    pub fn is_dirty(&self) -> bool {
        self.current.is_some() || !self.committed.is_empty()
    }

    // --- input (spec §7.2 push_pointer) ---

    /// Feeds one normalized pointer sample. Touch samples are ignored for
    /// drawing (palm rejection / pan is the shell's job, spec §5.2).
    pub fn push_pointer(&mut self, s: PointerSample) {
        if s.kind == PointerKind::Touch {
            return;
        }
        match s.phase {
            Phase::Down => {
                let mut stroke = Stroke::new(self.tool, self.size, self.color);
                stroke.opacity = self.opacity;
                stroke.erase = self.tool == Tool::Eraser;
                stroke.points.push(Point::new(s.x, s.y, s.pressure));
                self.current = Some(stroke);
            }
            Phase::Move => {
                if let Some(stroke) = self.current.as_mut() {
                    stroke.points.push(Point::new(s.x, s.y, s.pressure));
                }
            }
            Phase::Up => {
                if let Some(mut stroke) = self.current.take() {
                    stroke.points.push(Point::new(s.x, s.y, s.pressure));
                    self.commit(stroke);
                }
            }
        }
    }

    fn commit(&mut self, stroke: Stroke) {
        // Pressure-stuck guard (spec §5.4): a pen stroke whose pressure has no
        // variation likely means the driver fed a constant value.
        if stroke.tool == Tool::Pen
            && stroke.points.len() >= 3
            && !stroke.has_pressure_variation(self.pressure_variance_threshold)
        {
            self.pressure_stuck_warning = true;
        }
        bake_stroke(&mut self.layer, &stroke, &self.brush, self.smoothing);
        self.committed.push(Committed {
            stroke,
            brush: self.brush,
            smoothing: self.smoothing,
        });
        self.redo.clear();
    }

    // --- history (spec §7.2 undo/redo, stroke-level) ---

    pub fn undo(&mut self) {
        if let Some(c) = self.committed.pop() {
            self.redo.push(c);
            self.rebake();
        }
    }

    pub fn redo(&mut self) {
        if let Some(c) = self.redo.pop() {
            bake_stroke(&mut self.layer, &c.stroke, &c.brush, c.smoothing);
            self.committed.push(c);
        }
    }

    pub fn can_undo(&self) -> bool {
        !self.committed.is_empty()
    }
    pub fn can_redo(&self) -> bool {
        !self.redo.is_empty()
    }

    fn rebake(&mut self) {
        let (w, h) = (self.background.width, self.background.height);
        let strokes: Vec<Stroke> = self.committed.iter().map(|c| c.stroke.clone()).collect();
        let params: Vec<(Brush, Smoothing)> = self
            .committed
            .iter()
            .map(|c| (c.brush, c.smoothing))
            .collect();
        let mut it = params.into_iter();
        self.layer = rebake_layer(w, h, &strokes, |_| it.next().unwrap());
    }

    // --- display ---

    /// The flat image for display: background + committed strokes + the
    /// in-progress stroke. Correctness-first (spec §6.2 notes wgpu will replace
    /// this full-buffer path). Returns straight RGBA8.
    pub fn composite_for_display(&self) -> RgbaBuffer {
        let mut flat = flatten(&self.background, &self.layer);
        if let Some(stroke) = &self.current {
            bake_stroke(&mut flat, stroke, &self.brush, self.smoothing);
        }
        flat
    }

    /// The transparent strokes-only layer (committed strokes), for overlay.
    pub fn strokes_layer(&self) -> &RgbaBuffer {
        &self.layer
    }

    // --- export (spec §4.3 / §7.2) ---

    /// Builds the 3-file export from committed strokes. The in-progress stroke,
    /// if any, is not included (save = commit-first at the shell layer).
    pub fn export(&self) -> ExportSet {
        let flat = flatten(&self.background, &self.layer);
        let (w, h) = (self.background.width, self.background.height);
        let mut vector = VectorDoc::new(w, h);
        for c in &self.committed {
            vector
                .strokes
                .push(stroke_to_doc(&c.stroke, c.smoothing.as_tag()));
        }
        ExportSet {
            strokes_png: encode_png(&self.layer),
            flat_png: encode_png(&flat),
            vector,
        }
    }

    /// Loads a vector document, replacing history (spec §7.2 `load_vector`).
    /// Strokes are re-rasterised with the current brush/smoothing per stroke.
    pub fn load_vector(&mut self, doc: &VectorDoc) {
        self.committed.clear();
        self.redo.clear();
        self.current = None;
        for sd in &doc.strokes {
            let tool = match sd.kind.as_str() {
                "eraser" => Tool::Eraser,
                "line" => Tool::Line,
                "arrow" => Tool::Arrow,
                "rect" => Tool::Rect,
                "ellipse" => Tool::Ellipse,
                "text" => Tool::Text,
                _ => Tool::Pen,
            };
            let color = parse_css_color(&sd.color).unwrap_or(0xFF0000FF);
            let mut stroke = Stroke::new(tool, sd.size, color);
            stroke.opacity = sd.opacity;
            stroke.erase = sd.erase || tool == Tool::Eraser;
            stroke.points = sd
                .points
                .iter()
                .map(|p| Point::new(p.x, p.y, p.p))
                .collect();
            let brush = Brush::from_size(sd.size);
            let smoothing = Smoothing::from_tag(&sd.smoothing);
            self.committed.push(Committed {
                stroke,
                brush,
                smoothing,
            });
        }
        self.rebake();
    }
}

/// Parses `#rrggbb` (alpha assumed opaque) into packed `0xRRGGBBAA`.
fn parse_css_color(css: &str) -> Option<u32> {
    let h = css.strip_prefix('#')?;
    if h.len() != 6 {
        return None;
    }
    let r = u32::from_str_radix(&h[0..2], 16).ok()?;
    let g = u32::from_str_radix(&h[2..4], 16).ok()?;
    let b = u32::from_str_radix(&h[4..6], 16).ok()?;
    Some((r << 24) | (g << 16) | (b << 8) | 0xff)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::raster::pixel_at;

    fn down(x: f64, y: f64, p: f64) -> PointerSample {
        PointerSample {
            x,
            y,
            pressure: p,
            kind: PointerKind::Pen,
            phase: Phase::Down,
        }
    }
    fn mv(x: f64, y: f64, p: f64) -> PointerSample {
        PointerSample {
            x,
            y,
            pressure: p,
            kind: PointerKind::Pen,
            phase: Phase::Move,
        }
    }
    fn up(x: f64, y: f64, p: f64) -> PointerSample {
        PointerSample {
            x,
            y,
            pressure: p,
            kind: PointerKind::Pen,
            phase: Phase::Up,
        }
    }

    fn draw_line(e: &mut Engine, a: (f64, f64), b: (f64, f64), pr: f64) {
        e.push_pointer(down(a.0, a.1, pr));
        e.push_pointer(mv((a.0 + b.0) / 2.0, (a.1 + b.1) / 2.0, pr));
        e.push_pointer(up(b.0, b.1, pr));
    }

    #[test]
    fn drawing_then_export_yields_three_artifacts() {
        let mut e = Engine::new(64, 64);
        e.set_size(20.0);
        draw_line(&mut e, (10.0, 32.0), (54.0, 32.0), 1.0);
        let out = e.export();
        assert!(!out.strokes_png.is_empty());
        assert!(!out.flat_png.is_empty());
        assert_eq!(out.vector.strokes.len(), 1);
        assert_eq!(out.vector.strokes[0].kind, "pen");
        // Pressure recorded on every point.
        assert!(out.vector.strokes[0].points.iter().all(|p| p.p == 1.0));
    }

    #[test]
    fn undo_removes_the_last_stroke_from_the_layer() {
        let mut e = Engine::new(40, 40);
        e.set_size(16.0);
        draw_line(&mut e, (5.0, 20.0), (35.0, 20.0), 1.0);
        assert!(pixel_at(e.strokes_layer(), 20, 20).3 > 100, "drawn");
        e.undo();
        assert_eq!(pixel_at(e.strokes_layer(), 20, 20).3, 0, "undone");
        assert!(e.can_redo());
        e.redo();
        assert!(pixel_at(e.strokes_layer(), 20, 20).3 > 100, "redone");
    }

    #[test]
    fn new_stroke_clears_redo() {
        let mut e = Engine::new(40, 40);
        e.set_size(10.0);
        draw_line(&mut e, (5.0, 10.0), (35.0, 10.0), 1.0);
        e.undo();
        assert!(e.can_redo());
        draw_line(&mut e, (5.0, 30.0), (35.0, 30.0), 1.0);
        assert!(!e.can_redo(), "a fresh stroke discards the redo stack");
    }

    #[test]
    fn eraser_after_pen_leaves_a_hole() {
        let mut e = Engine::new(40, 40);
        e.set_size(16.0);
        draw_line(&mut e, (5.0, 20.0), (35.0, 20.0), 1.0);
        e.set_tool(Tool::Eraser);
        e.set_size(24.0);
        draw_line(&mut e, (18.0, 20.0), (22.0, 20.0), 1.0);
        assert!(pixel_at(e.strokes_layer(), 20, 20).3 < 20, "erased hole");
        assert_eq!(e.export().vector.strokes.len(), 2);
        assert_eq!(e.export().vector.strokes[1].kind, "eraser");
    }

    #[test]
    fn touch_samples_do_not_draw() {
        let mut e = Engine::new(20, 20);
        e.push_pointer(PointerSample {
            x: 10.0,
            y: 10.0,
            pressure: 1.0,
            kind: PointerKind::Touch,
            phase: Phase::Down,
        });
        e.push_pointer(PointerSample {
            x: 10.0,
            y: 10.0,
            pressure: 1.0,
            kind: PointerKind::Touch,
            phase: Phase::Up,
        });
        assert!(!e.is_dirty(), "touch must not create a stroke");
    }

    #[test]
    fn constant_pressure_raises_stuck_warning() {
        let mut e = Engine::new(60, 20);
        e.set_size(10.0);
        // Many-point constant-pressure pen stroke.
        e.push_pointer(down(5.0, 10.0, 0.5));
        for x in 1..10 {
            e.push_pointer(mv(5.0 + x as f64 * 5.0, 10.0, 0.5));
        }
        e.push_pointer(up(55.0, 10.0, 0.5));
        assert!(e.pressure_stuck_warning, "constant pressure must warn");
    }

    #[test]
    fn varying_pressure_does_not_warn() {
        let mut e = Engine::new(60, 20);
        e.set_size(10.0);
        e.push_pointer(down(5.0, 10.0, 0.1));
        for x in 1..10 {
            e.push_pointer(mv(5.0 + x as f64 * 5.0, 10.0, 0.1 + 0.08 * x as f64));
        }
        e.push_pointer(up(55.0, 10.0, 0.95));
        assert!(!e.pressure_stuck_warning);
    }

    #[test]
    fn export_then_load_vector_roundtrips_strokes() {
        let mut e = Engine::new(50, 50);
        e.set_size(12.0);
        draw_line(&mut e, (5.0, 25.0), (45.0, 25.0), 0.8);
        let json = e.export().vector.to_json().unwrap();

        let doc = VectorDoc::from_json(&json).unwrap();
        let mut e2 = Engine::new(50, 50);
        e2.load_vector(&doc);
        let re = e2.export();
        assert_eq!(re.vector.strokes.len(), 1);
        assert_eq!(re.vector.strokes[0].points.len(), 3);
        assert!(re.vector.strokes[0].points.iter().all(|p| p.p == 0.8));
        // Re-rasterised layer has ink where the line was.
        assert!(pixel_at(e2.strokes_layer(), 25, 25).3 > 50);
    }

    #[test]
    fn dirty_flag_tracks_committed_work() {
        let mut e = Engine::new(30, 30);
        assert!(!e.is_dirty());
        e.set_size(8.0);
        draw_line(&mut e, (5.0, 15.0), (25.0, 15.0), 1.0);
        assert!(e.is_dirty());
        e.undo();
        assert!(!e.is_dirty(), "after undoing the only stroke");
    }
}
