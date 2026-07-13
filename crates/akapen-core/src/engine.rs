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

/// Tells a GPU (or any incremental) consumer how the last mutating call
/// affects the offscreen-baked texture (spec §6.2 point 3: "確定ストロークの
/// オフスクリーン焼き込み" — commit appends one stroke in O(1); undo/redo
/// rebuild). Lets the consumer decide in O(1) whether it can draw just one
/// more stroke into its existing texture or must re-bake from scratch.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BakeDelta {
    /// No committed-stroke change occurred (e.g. an ignored touch sample, or
    /// a `Down`/`Move` sample that only extended the in-progress stroke).
    None,
    /// Exactly one stroke was appended to the baked layer/texture. Mirrors
    /// `raster.rs::bake_stroke`'s incremental (non-rebaking) path.
    Append,
    /// The full committed history was (or must be) re-baked from scratch.
    /// Mirrors `raster.rs::rebake_layer`.
    Rebuild,
}

/// A borrowed view of one stroke plus the brush/smoothing it was (or is
/// being) drawn with — enough for a consumer to call
/// [`crate::tessellate::tessellate_stroke`] without cloning engine state.
#[derive(Debug, Clone, Copy)]
pub struct CommittedStrokeRef<'a> {
    pub stroke: &'a Stroke,
    pub brush: &'a Brush,
    pub smoothing: Smoothing,
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

    /// Borrowed view of the background bitmap (straight-alpha RGBA8, natural
    /// size), for a GPU consumer to upload as its initial background texture
    /// (spec §6.2 point 3 / Phase e FFI wiring). Read-only accessor added for
    /// the render path; the field itself and its meaning are unchanged. The
    /// backgrounds this engine holds are effectively opaque (alpha = 255), so
    /// uploading them into a premultiplied-alpha pipeline
    /// ([`akapen_render::BackgroundPipeline::set_background`]) is a no-op
    /// difference from straight alpha.
    pub fn background(&self) -> &RgbaBuffer {
        &self.background
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
    ///
    /// Returns a [`BakeDelta`] so a GPU consumer can decide in O(1) whether
    /// to append the just-committed stroke to its offscreen texture (`Up`
    /// that closes a stroke) or do nothing (`Down`/`Move`, or an ignored
    /// touch sample).
    pub fn push_pointer(&mut self, s: PointerSample) -> BakeDelta {
        if s.kind == PointerKind::Touch {
            return BakeDelta::None;
        }
        match s.phase {
            Phase::Down => {
                let mut stroke = Stroke::new(self.tool, self.size, self.color);
                stroke.opacity = self.opacity;
                stroke.erase = self.tool == Tool::Eraser;
                stroke.points.push(Point::new(s.x, s.y, s.pressure));
                self.current = Some(stroke);
                BakeDelta::None
            }
            Phase::Move => {
                if let Some(stroke) = self.current.as_mut() {
                    stroke.points.push(Point::new(s.x, s.y, s.pressure));
                }
                BakeDelta::None
            }
            Phase::Up => {
                if let Some(mut stroke) = self.current.take() {
                    stroke.points.push(Point::new(s.x, s.y, s.pressure));
                    self.commit(stroke)
                } else {
                    BakeDelta::None
                }
            }
        }
    }

    fn commit(&mut self, stroke: Stroke) -> BakeDelta {
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
        BakeDelta::Append
    }

    // --- history (spec §7.2 undo/redo, stroke-level) ---

    /// Undoes the last committed stroke. Returns [`BakeDelta::Rebuild`] when
    /// a stroke was undone (the layer was rebaked from the remaining
    /// history), or [`BakeDelta::None`] when there was nothing to undo.
    pub fn undo(&mut self) -> BakeDelta {
        if let Some(c) = self.committed.pop() {
            self.redo.push(c);
            self.rebake();
            BakeDelta::Rebuild
        } else {
            BakeDelta::None
        }
    }

    /// Redoes the last undone stroke. Redo re-bakes incrementally (mirrors
    /// `commit`), so this returns [`BakeDelta::Append`] when a stroke was
    /// redone, or [`BakeDelta::None`] when there was nothing to redo.
    pub fn redo(&mut self) -> BakeDelta {
        if let Some(c) = self.redo.pop() {
            bake_stroke(&mut self.layer, &c.stroke, &c.brush, c.smoothing);
            self.committed.push(c);
            BakeDelta::Append
        } else {
            BakeDelta::None
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

    // --- GPU-consumer accessors (spec §7.2 / §6.2 point 3) ---

    /// Borrowed view of the committed strokes, each paired with the
    /// brush/smoothing it was drawn with (1:1 with what `rebake()` feeds
    /// `rebake_layer`). A [`BakeDelta::Rebuild`] consumer re-tessellates
    /// this whole sequence; a [`BakeDelta::Append`] consumer only needs the
    /// last entry — and can fetch it in O(1) via `.next_back()`
    /// ([`DoubleEndedIterator`], backed by `Vec`'s slice iterator) instead of
    /// draining the whole iterator with `.last()` (review perf finding used
    /// by `akapen_render::plan_bake`'s `Append` arm: `.last()` was O(n) per
    /// commit, O(n²) over a whole session).
    pub fn committed_strokes(
        &self,
    ) -> impl ExactSizeIterator<Item = CommittedStrokeRef<'_>> + DoubleEndedIterator {
        self.committed.iter().map(|c| CommittedStrokeRef {
            stroke: &c.stroke,
            brush: &c.brush,
            smoothing: c.smoothing,
        })
    }

    /// The in-progress ("wet ink") stroke, if any, paired with the *current*
    /// tool's brush/smoothing (1:1 with what `composite_for_display` bakes
    /// it with). `None` when no stroke is in progress.
    pub fn current_stroke(&self) -> Option<CommittedStrokeRef<'_>> {
        self.current.as_ref().map(|stroke| CommittedStrokeRef {
            stroke,
            brush: &self.brush,
            smoothing: self.smoothing,
        })
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

    #[derive(serde::Deserialize)]
    struct GoldenExport {
        canvas: GoldenCanvas,
        commands: Vec<GoldenCommand>,
        expected: GoldenExpected,
    }

    #[derive(serde::Deserialize)]
    struct GoldenCanvas {
        width: u32,
        height: u32,
    }

    #[derive(serde::Deserialize)]
    struct GoldenCommand {
        x: f64,
        y: f64,
        pressure: f64,
        kind: String,
        phase: String,
    }

    #[derive(serde::Deserialize)]
    struct GoldenExpected {
        schema: String,
        natural_w: u32,
        natural_h: u32,
        stroke_count: usize,
        kinds: Vec<String>,
        points: Vec<Vec<crate::vector::VectorPoint>>,
    }

    #[test]
    fn shared_golden_commands_produce_expected_export() {
        let fixture: GoldenExport =
            serde_json::from_str(include_str!("../../../testdata/golden-export-v1.json"))
                .expect("golden fixture must be valid JSON");
        let mut engine = Engine::new(fixture.canvas.width, fixture.canvas.height);
        for command in fixture.commands {
            assert_eq!(command.kind, "pen");
            let phase = match command.phase.as_str() {
                "down" => Phase::Down,
                "move" => Phase::Move,
                "up" => Phase::Up,
                other => panic!("unknown golden phase: {other}"),
            };
            engine.push_pointer(PointerSample {
                x: command.x,
                y: command.y,
                pressure: command.pressure,
                kind: PointerKind::Pen,
                phase,
            });
        }

        let doc = engine.export().vector;
        assert_eq!(doc.schema, fixture.expected.schema);
        assert_eq!(
            (doc.natural_w, doc.natural_h),
            (fixture.expected.natural_w, fixture.expected.natural_h)
        );
        assert_eq!(doc.strokes.len(), fixture.expected.stroke_count);
        assert_eq!(
            doc.strokes
                .iter()
                .map(|stroke| stroke.kind.as_str())
                .collect::<Vec<_>>(),
            fixture
                .expected
                .kinds
                .iter()
                .map(String::as_str)
                .collect::<Vec<_>>()
        );
        for (actual, expected) in doc.strokes.iter().zip(fixture.expected.points) {
            assert_eq!(actual.points, expected);
            assert!(actual.points.iter().all(|point| point.p > 0.0));
        }
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

    // --- BakeDelta / accessors (Phase 0) ---

    #[test]
    fn push_pointer_reports_append_only_on_commit() {
        let mut e = Engine::new(30, 30);
        e.set_size(8.0);
        assert_eq!(e.push_pointer(down(5.0, 5.0, 1.0)), BakeDelta::None);
        assert_eq!(e.push_pointer(mv(10.0, 5.0, 1.0)), BakeDelta::None);
        assert_eq!(e.push_pointer(up(15.0, 5.0, 1.0)), BakeDelta::Append);
    }

    #[test]
    fn touch_sample_reports_none() {
        let mut e = Engine::new(20, 20);
        let touch = PointerSample {
            x: 10.0,
            y: 10.0,
            pressure: 1.0,
            kind: PointerKind::Touch,
            phase: Phase::Down,
        };
        assert_eq!(e.push_pointer(touch), BakeDelta::None);
    }

    #[test]
    fn stray_up_without_down_reports_none() {
        // No preceding Down: current is None, so Up has nothing to commit.
        let mut e = Engine::new(20, 20);
        assert_eq!(e.push_pointer(up(5.0, 5.0, 1.0)), BakeDelta::None);
    }

    #[test]
    fn undo_redo_report_rebuild_and_append_or_none() {
        let mut e = Engine::new(30, 30);
        e.set_size(8.0);
        // Nothing committed yet.
        assert_eq!(e.undo(), BakeDelta::None);
        assert_eq!(e.redo(), BakeDelta::None);

        draw_line(&mut e, (5.0, 15.0), (25.0, 15.0), 1.0);
        assert_eq!(e.undo(), BakeDelta::Rebuild);
        assert_eq!(e.undo(), BakeDelta::None, "already empty");
        assert_eq!(e.redo(), BakeDelta::Append);
        assert_eq!(e.redo(), BakeDelta::None, "nothing left to redo");
    }

    #[test]
    fn committed_strokes_accessor_matches_history() {
        let mut e = Engine::new(30, 30);
        e.set_size(8.0);
        draw_line(&mut e, (5.0, 15.0), (25.0, 15.0), 1.0);
        e.set_tool(Tool::Eraser);
        e.set_size(12.0);
        draw_line(&mut e, (10.0, 15.0), (20.0, 15.0), 1.0);

        let refs: Vec<_> = e.committed_strokes().collect();
        assert_eq!(refs.len(), 2);
        assert_eq!(refs.len(), e.committed_strokes().len(), "ExactSizeIterator");
        assert_eq!(refs[0].stroke.tool, Tool::Pen);
        assert_eq!(refs[1].stroke.tool, Tool::Eraser);
        // The brush recorded per stroke reflects the size active when drawn.
        assert!((refs[0].brush.max_width - 8.0).abs() < 1e-9);
        assert!((refs[1].brush.max_width - 12.0).abs() < 1e-9);
    }

    #[test]
    fn current_stroke_accessor_reflects_in_progress_stroke() {
        let mut e = Engine::new(30, 30);
        e.set_size(9.0);
        assert!(e.current_stroke().is_none(), "nothing in progress yet");

        e.push_pointer(down(5.0, 5.0, 0.4));
        let cur = e.current_stroke().expect("stroke in progress");
        assert_eq!(cur.stroke.points.len(), 1);
        assert!((cur.brush.max_width - 9.0).abs() < 1e-9);

        e.push_pointer(up(15.0, 5.0, 0.4));
        assert!(
            e.current_stroke().is_none(),
            "committed, nothing in progress"
        );
    }
}
