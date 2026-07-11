//! Phase d: the offscreen `baked_tex` (spec §6.2 point 3 "確定ストロークの
//! オフスクリーン焼き込み") and the per-frame composite it enables.
//!
//! [`BakedTexture`] holds every *committed* stroke, drawn once each; the
//! per-frame cost of displaying them is then O(1) in stroke count — draw the
//! background, draw `baked_tex` once as a textured quad, draw the one
//! in-progress ("wet ink") stroke, done ([`composite_frame`]).
//! [`Engine::push_pointer`]/`undo`/`redo` already tell the caller, via
//! [`BakeDelta`], whether `baked_tex` needs a single incremental append or a
//! full rebuild ([`akapen_core::engine`] module doc); [`plan_bake`] turns
//! that signal plus the engine's committed-stroke history into a concrete,
//! GPU-independent plan ([`BakePlan`]), and [`apply_bake_delta`] is the thin
//! GPU-touching executor of that plan. Splitting the two like this is what
//! makes the "which strokes get (re)drawn" decision unit-testable without a
//! device — see the `plan_bake` tests below, which exercise a real
//! [`akapen_core::engine::Engine`] and assert on [`BakePlan`] alone.

use akapen_core::coord::ViewTransform;
use akapen_core::engine::{BakeDelta, CommittedStrokeRef};
use akapen_core::tessellate::tessellate_stroke;

use crate::background::{BackgroundPipeline, BackgroundTexture};
use crate::renderer::{read_rgba_from_texture, OFFSCREEN_FORMAT};
use crate::stroke::{is_erase, StrokePipeline};
use crate::transform::identity_view;

/// The offscreen "baked" texture: every committed stroke, drawn once,
/// composited together at the image's natural size (spec: "画像原寸、透明
/// クリア始点"). Drawn *into* by [`apply_bake_delta`]; drawn *from* (as a
/// textured quad, through the real on-screen [`ViewTransform`]) by
/// [`composite_frame`].
pub struct BakedTexture {
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    width: u32,
    height: u32,
}

impl BakedTexture {
    /// A transparent `width x height` baked texture (the image's natural
    /// size — not the on-screen output size, which can differ under
    /// zoom/pan). Usage covers all three roles this texture plays: a render
    /// target (strokes get drawn into it), a sampled texture (the per-frame
    /// composite reads it), and `COPY_SRC` (test readback).
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue, width: u32, height: u32) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("akapen-render baked stroke texture"),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: OFFSCREEN_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        let baked = Self {
            texture,
            view,
            width,
            height,
        };
        baked.clear(device, queue);
        baked
    }

    pub fn width(&self) -> u32 {
        self.width
    }
    pub fn height(&self) -> u32 {
        self.height
    }
    pub fn view(&self) -> &wgpu::TextureView {
        &self.view
    }

    /// Clears every committed stroke back to fully transparent — the "始点"
    /// (starting point) a [`BakePlan::Rebuild`] with no strokes left
    /// redraws from (spec: undo down to nothing must not leave stale ink),
    /// also used to establish the initial transparent state in [`Self::new`].
    pub fn clear(&self, device: &wgpu::Device, queue: &wgpu::Queue) {
        let mut encoder =
            device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
        {
            let _pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("akapen-render baked clear pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &self.view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
        }
        queue.submit(Some(encoder.finish()));
    }

    /// Test/inspection readback — see [`crate::renderer::read_rgba_from_texture`].
    pub fn read_rgba(&self, device: &wgpu::Device, queue: &wgpu::Queue) -> Vec<u8> {
        read_rgba_from_texture(device, queue, &self.texture, self.width, self.height)
    }
}

/// Pure decision of which committed stroke(s) must be (re)drawn into
/// `baked_tex` for a given [`BakeDelta`], decoupled from any GPU calls so
/// it's unit-testable without a device (spec instruction: "純ロジック(焼き
/// 込み対象決定=BakeDelta反映)は関数に切り出す"). [`apply_bake_delta`] is the
/// GPU-touching executor of whatever this returns.
#[derive(Debug)]
pub enum BakePlan<T> {
    /// Nothing to draw (`BakeDelta::None`: an ignored/in-progress sample).
    Skip,
    /// Append exactly this one stroke onto `baked_tex`'s existing contents
    /// (`BakeDelta::Append`: a commit or a redo).
    Append(T),
    /// Clear `baked_tex` and redraw every entry, in order (`BakeDelta::
    /// Rebuild`: an undo). An empty `Vec` means every stroke was undone —
    /// `baked_tex` must end up transparent, not merely "not redrawn".
    Rebuild(Vec<T>),
}

/// Builds a [`BakePlan`] from an [`Engine`](akapen_core::engine::Engine)'s
/// [`BakeDelta`] (as returned by `push_pointer`/`undo`/`redo`) and its
/// current [`Engine::committed_strokes`](akapen_core::engine::Engine::committed_strokes)
/// iterator.
///
/// `Append` fetches only the last entry via [`DoubleEndedIterator::
/// next_back`] — O(1) — rather than `Iterator::last()`, which would drain
/// the whole iterator from the front (review perf finding: at O(n) per
/// commit, a whole drawing session of n strokes cost O(n²) overall). `Rebuild`
/// is unaffected: redrawing every committed stroke is inherently O(n).
pub fn plan_bake<'a, I>(delta: BakeDelta, mut committed: I) -> BakePlan<CommittedStrokeRef<'a>>
where
    I: DoubleEndedIterator<Item = CommittedStrokeRef<'a>>,
{
    match delta {
        BakeDelta::None => BakePlan::Skip,
        BakeDelta::Append => match committed.next_back() {
            Some(c) => BakePlan::Append(c),
            // Defensive: the engine only ever reports `Append` right after a
            // commit/redo, which always leaves at least one committed
            // stroke — this arm should be unreachable in practice.
            None => BakePlan::Skip,
        },
        BakeDelta::Rebuild => BakePlan::Rebuild(committed.collect()),
    }
}

/// Executes a [`BakePlan`] against `baked`: the GPU-touching half of the
/// `BakeDelta` -> `baked_tex` pipeline (pure decision-making lives in
/// [`plan_bake`]).
pub fn apply_bake_delta(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    pipeline: &StrokePipeline,
    baked: &BakedTexture,
    plan: BakePlan<CommittedStrokeRef<'_>>,
) {
    match plan {
        BakePlan::Skip => {}
        BakePlan::Append(c) => {
            draw_committed_into_baked(device, queue, pipeline, baked, &c, wgpu::LoadOp::Load)
        }
        BakePlan::Rebuild(list) => {
            if list.is_empty() {
                baked.clear(device, queue);
                return;
            }
            let mut load = wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT);
            for c in &list {
                draw_committed_into_baked(device, queue, pipeline, baked, c, load);
                load = wgpu::LoadOp::Load;
            }
        }
    }
}

fn draw_committed_into_baked(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    pipeline: &StrokePipeline,
    baked: &BakedTexture,
    c: &CommittedStrokeRef<'_>,
    load: wgpu::LoadOp<wgpu::Color>,
) {
    let verts = tessellate_stroke(c.stroke, c.brush, c.smoothing);
    // baked_tex is at the image's natural size and holds strokes in the same
    // image-pixel space they were tessellated in — 1:1, no zoom/pan/rotation
    // (that only applies once, at the final on-screen composite step).
    let view = identity_view(baked.width() as f64, baked.height() as f64);
    pipeline.draw(
        device,
        queue,
        baked.view(),
        baked.width(),
        baked.height(),
        &verts,
        c.stroke.color,
        c.stroke.opacity,
        is_erase(c.stroke),
        &view,
        load,
    );
}

/// Draws one full frame: background -> `baked_tex` -> wet ink, in that
/// order, all positioned by the same on-screen `view` (spec §6.2 point 3:
/// "毎フレームは「焼き込み済みテクスチャ1枚 + 描画中の1ストローク」だけを
/// 合成する"). Cost per frame is independent of committed-stroke count —
/// `baked_tex` is always exactly one textured-quad draw, regardless of how
/// many strokes are baked into it.
///
/// `wet`, if `Some`, is the in-progress stroke
/// ([`Engine::current_stroke`](akapen_core::engine::Engine::current_stroke)).
/// An in-progress **eraser** stroke is intentionally *not* drawn here (M1
/// simplification, spec instruction: "消しゴムの描画中プレビューはM1簡易
/// 方針(commit時にbakedへ反映すれば可、描画中のbaked消去プレビューは今回
/// 作り込まない)") — its effect only appears once committed, via the next
/// `BakeDelta::Append` into `baked_tex`. A wet **pen** stroke is always drawn
/// live.
///
/// Records background, `baked_tex`, and (if present) the wet stroke as three
/// draw calls inside a **single render pass**, submitted once (review perf
/// finding: this used to be `background_pipeline.render` +
/// `.render_texture` + `stroke_pipeline.draw`, three separate encoders/
/// passes/submits, each rebuilding its own uniform — and for the wet stroke,
/// vertex — buffer). A `wgpu::RenderPass` borrows whatever resources it
/// records for its own lifetime, so every draw's uniform buffer/bind group
/// (and the wet stroke's vertex buffer) must already exist *before* the pass
/// opens — hence preparing all three up front via [`BackgroundPipeline::
/// prepare_texture_draw`]/[`StrokePipeline::prepare_draw`] and only then
/// opening the one pass that records them via `record_texture_draw`/
/// `record_draw`.
#[allow(clippy::too_many_arguments)]
pub fn composite_frame(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    target_view: &wgpu::TextureView,
    output_w: u32,
    output_h: u32,
    background_pipeline: &BackgroundPipeline,
    background: &BackgroundTexture,
    baked: &BakedTexture,
    stroke_pipeline: &StrokePipeline,
    wet: Option<CommittedStrokeRef<'_>>,
    view: &ViewTransform,
) {
    let background_draw = background_pipeline.prepare_texture_draw(
        device,
        queue,
        output_w,
        output_h,
        background.view(),
        view,
    );
    let baked_draw = background_pipeline.prepare_texture_draw(
        device,
        queue,
        output_w,
        output_h,
        baked.view(),
        view,
    );
    // An in-progress eraser stroke is never drawn live (M1 simplification,
    // see this function's doc) — filtered out here so there is simply no
    // third prepared draw to record below.
    let wet_draw = wet.filter(|w| !is_erase(w.stroke)).map(|w| {
        let verts = tessellate_stroke(w.stroke, w.brush, w.smoothing);
        stroke_pipeline.prepare_draw(
            device,
            queue,
            output_w,
            output_h,
            &verts,
            w.stroke.color,
            w.stroke.opacity,
            false,
            view,
        )
    });

    let mut encoder =
        device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
    {
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("akapen-render composite pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: target_view,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    // Background is the bottom-most layer — nothing to
                    // preserve underneath yet, matching
                    // `BackgroundPipeline::render`'s own always-clear.
                    load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        });
        background_pipeline.record_texture_draw(&mut pass, &background_draw);
        background_pipeline.record_texture_draw(&mut pass, &baked_draw);
        if let Some(wet_draw) = &wet_draw {
            stroke_pipeline.record_draw(&mut pass, wet_draw);
        }
    }
    queue.submit(Some(encoder.finish()));
}

#[cfg(test)]
mod tests {
    use super::*;
    use akapen_core::engine::{Engine, Phase, PointerSample};
    use akapen_core::raster::{flatten, pixel_at, RgbaBuffer};
    use akapen_core::stroke::PointerKind;

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
    fn draw_line(e: &mut Engine, a: (f64, f64), b: (f64, f64), pr: f64) -> BakeDelta {
        e.push_pointer(down(a.0, a.1, pr));
        e.push_pointer(mv((a.0 + b.0) / 2.0, (a.1 + b.1) / 2.0, pr));
        e.push_pointer(up(b.0, b.1, pr))
    }

    // ── plan_bake: pure, no GPU needed ──

    #[test]
    fn none_delta_plans_to_skip() {
        let mut e = Engine::new(30, 30);
        e.set_size(8.0);
        let delta = e.push_pointer(down(5.0, 5.0, 1.0));
        assert_eq!(delta, BakeDelta::None);
        assert!(matches!(
            plan_bake(delta, e.committed_strokes()),
            BakePlan::Skip
        ));
    }

    #[test]
    fn append_delta_plans_to_append_the_just_committed_stroke() {
        let mut e = Engine::new(30, 30);
        e.set_size(8.0);
        let delta = draw_line(&mut e, (5.0, 5.0), (15.0, 5.0), 1.0);
        assert_eq!(delta, BakeDelta::Append);
        match plan_bake(delta, e.committed_strokes()) {
            BakePlan::Append(c) => assert_eq!(c.stroke.points.len(), 3),
            other => panic!("expected Append, got {other:?}"),
        }
    }

    #[test]
    fn rebuild_delta_plans_to_redraw_remaining_committed_history() {
        let mut e = Engine::new(30, 30);
        e.set_size(8.0);
        draw_line(&mut e, (5.0, 5.0), (15.0, 5.0), 1.0);
        draw_line(&mut e, (5.0, 15.0), (15.0, 15.0), 1.0);
        let delta = e.undo();
        assert_eq!(delta, BakeDelta::Rebuild);
        match plan_bake(delta, e.committed_strokes()) {
            BakePlan::Rebuild(list) => {
                assert_eq!(list.len(), 1, "one stroke remains after undoing the second")
            }
            other => panic!("expected Rebuild, got {other:?}"),
        }
    }

    #[test]
    fn rebuild_delta_plans_to_an_empty_list_once_everything_is_undone() {
        let mut e = Engine::new(30, 30);
        e.set_size(8.0);
        draw_line(&mut e, (5.0, 5.0), (15.0, 5.0), 1.0);
        let delta = e.undo();
        match plan_bake(delta, e.committed_strokes()) {
            BakePlan::Rebuild(list) => assert!(list.is_empty(), "baked_tex must end up empty too"),
            other => panic!("expected Rebuild, got {other:?}"),
        }
    }

    #[test]
    fn redo_delta_plans_to_append_the_redone_stroke() {
        let mut e = Engine::new(30, 30);
        e.set_size(8.0);
        draw_line(&mut e, (5.0, 5.0), (15.0, 5.0), 1.0);
        e.undo();
        let delta = e.redo();
        assert_eq!(delta, BakeDelta::Append);
        match plan_bake(delta, e.committed_strokes()) {
            BakePlan::Append(c) => assert_eq!(c.stroke.points.len(), 3),
            other => panic!("expected Append, got {other:?}"),
        }
    }

    // ── GPU: apply_bake_delta + composite_frame ──

    use crate::test_support::try_headless_renderer;

    /// Phase d AC: draw several strokes, undo the last one, and check that a
    /// readback of the composited frame matches the CPU oracle
    /// (`akapen_core::raster::flatten`, exactly what `Engine::composite_for_
    /// display` uses) at representative *interior* points — the stroke
    /// centers, not their tessellated-triangle edges (this GPU path never
    /// claimed pixel-exact edge antialiasing; see `stroke.rs` module doc).
    /// Also checks the undone stroke's own center reverts to plain
    /// background, proving the undo→Rebuild path actually removed it from
    /// `baked_tex` rather than just skipping the redraw.
    #[test]
    fn undo_readback_matches_cpu_flatten_oracle_at_stroke_centers() {
        let Some(renderer) =
            try_headless_renderer("undo_readback_matches_cpu_flatten_oracle_at_stroke_centers")
        else {
            return;
        };
        let size = 64u32;

        // CPU oracle: an Engine over the same opaque-white natural background
        // `BakedTexture`'s GPU counterpart below is fed manually (Engine has
        // no background accessor to reuse directly — see report note).
        let mut engine = Engine::new(size, size);
        let background_rgba = vec![255u8; (size * size * 4) as usize];

        let device = &renderer.device;
        let queue = &renderer.queue;
        let stroke_pipeline = StrokePipeline::new(device, OFFSCREEN_FORMAT);
        let background_pipeline = BackgroundPipeline::new(device, OFFSCREEN_FORMAT);
        let background_tex =
            background_pipeline.set_background(device, queue, &background_rgba, size, size);
        let baked = BakedTexture::new(device, queue, size, size);

        // Stroke A (kept): red, centered at y=16.
        engine.set_color(0xFF0000FF);
        engine.set_size(12.0);
        let delta_a = draw_line(&mut engine, (10.0, 16.0), (54.0, 16.0), 1.0);
        apply_bake_delta(
            device,
            queue,
            &stroke_pipeline,
            &baked,
            plan_bake(delta_a, engine.committed_strokes()),
        );

        // Stroke B (undone below): green, centered at y=48.
        engine.set_color(0x00FF00FF);
        let delta_b = draw_line(&mut engine, (10.0, 48.0), (54.0, 48.0), 1.0);
        apply_bake_delta(
            device,
            queue,
            &stroke_pipeline,
            &baked,
            plan_bake(delta_b, engine.committed_strokes()),
        );

        let delta_undo = engine.undo();
        apply_bake_delta(
            device,
            queue,
            &stroke_pipeline,
            &baked,
            plan_bake(delta_undo, engine.committed_strokes()),
        );

        // GPU composite, no wet stroke in progress.
        let target = crate::renderer::OffscreenTarget::new(device, size, size);
        let view = identity_view(size as f64, size as f64);
        composite_frame(
            device,
            queue,
            &target.view,
            size,
            size,
            &background_pipeline,
            &background_tex,
            &baked,
            &stroke_pipeline,
            None,
            &view,
        );
        let gpu = target.read_rgba(device, queue);

        // CPU oracle: background + committed layer (stroke A only — B was undone).
        let background_buf = RgbaBuffer::from_rgba(size, size, background_rgba);
        let cpu = flatten(&background_buf, engine.strokes_layer());
        assert_eq!(
            engine.composite_for_display().data,
            cpu.data,
            "sanity: no wet stroke in progress, so composite_for_display == flatten"
        );

        let gpu_pixel = |x: u32, y: u32| -> [u8; 4] {
            let i = ((y * size + x) * 4) as usize;
            [gpu[i], gpu[i + 1], gpu[i + 2], gpu[i + 3]]
        };
        const TOLERANCE: i32 = 4;
        let assert_close = |x: u32, y: u32, label: &str| {
            let got = gpu_pixel(x, y);
            let (er, eg, eb, ea) = pixel_at(&cpu, x, y);
            let expected = [er, eg, eb, ea];
            for c in 0..4 {
                let diff = (got[c] as i32 - expected[c] as i32).abs();
                assert!(
                    diff <= TOLERANCE,
                    "{label} ({x},{y}) channel {c}: expected {}, got {} (diff {diff})",
                    expected[c],
                    got[c]
                );
            }
        };

        // Stroke A's center: still baked in (kept), must be red.
        assert_close(32, 16, "kept stroke A center");
        assert_eq!(
            gpu_pixel(32, 16),
            [255, 0, 0, 255],
            "stroke A center is opaque red"
        );

        // Stroke B's center: undone, must have reverted to background white —
        // proves Rebuild actually dropped it from baked_tex, not just that
        // the readback happens to tolerate stale green under a wide margin.
        assert_close(32, 48, "undone stroke B center");
        assert_eq!(
            gpu_pixel(32, 48),
            [255, 255, 255, 255],
            "stroke B center reverted to background"
        );

        // Untouched background corner.
        assert_close(2, 2, "untouched background corner");
    }

    /// Phase d AC (wet-ink-not-yet-committed): a stroke still in progress
    /// (never committed, so never in `baked_tex`) shows up in the composited
    /// frame via the `wet` overlay, at its own interior point — proving
    /// `composite_frame`'s third layer actually contributes, not just
    /// background+baked.
    #[test]
    fn in_progress_wet_stroke_shows_in_the_composited_frame() {
        let Some(renderer) =
            try_headless_renderer("in_progress_wet_stroke_shows_in_the_composited_frame")
        else {
            return;
        };
        let size = 64u32;
        let mut engine = Engine::new(size, size);
        let background_rgba = vec![255u8; (size * size * 4) as usize];

        let device = &renderer.device;
        let queue = &renderer.queue;
        let stroke_pipeline = StrokePipeline::new(device, OFFSCREEN_FORMAT);
        let background_pipeline = BackgroundPipeline::new(device, OFFSCREEN_FORMAT);
        let background_tex =
            background_pipeline.set_background(device, queue, &background_rgba, size, size);
        let baked = BakedTexture::new(device, queue, size, size);

        engine.set_color(0x0000FFFF); // blue
        engine.set_size(14.0);
        // Down + Move only: still in progress, never committed.
        engine.push_pointer(down(10.0, 32.0, 1.0));
        engine.push_pointer(mv(54.0, 32.0, 1.0));
        assert!(engine.current_stroke().is_some());

        let target = crate::renderer::OffscreenTarget::new(device, size, size);
        let view = identity_view(size as f64, size as f64);
        composite_frame(
            device,
            queue,
            &target.view,
            size,
            size,
            &background_pipeline,
            &background_tex,
            &baked,
            &stroke_pipeline,
            engine.current_stroke(),
            &view,
        );
        let gpu = target.read_rgba(device, queue);
        let gpu_pixel = |x: u32, y: u32| -> [u8; 4] {
            let i = ((y * size + x) * 4) as usize;
            [gpu[i], gpu[i + 1], gpu[i + 2], gpu[i + 3]]
        };
        assert_eq!(
            gpu_pixel(32, 32),
            [0, 0, 255, 255],
            "wet stroke center is opaque blue"
        );
        assert_eq!(
            gpu_pixel(2, 2),
            [255, 255, 255, 255],
            "untouched corner is still background"
        );
    }
}
